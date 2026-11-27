using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Nebula.Models;

namespace Nebula.Services;

/// <summary>
/// La parte "intelligente" della barra di ricerca: riconosce calcoli, comandi in
/// italiano per Nebula e per Windows, e — se configurato — chiede una risposta
/// all'AI. Non fa ricerche web: o esegue qualcosa in locale, o risponde il
/// modello scelto dall'utente.
/// </summary>
public static class SmartSearchService
{
    private static readonly CultureInfo Italian = CultureInfo.GetCultureInfo("it-IT");

    // Un solo client per le richieste reali: creare un HttpClient per domanda
    // esaurisce le porte sotto uso intenso.
    private static readonly HttpClient SharedHttp = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    private const string SystemPrompt =
        "Sei l'assistente della barra rapida di Nebula, un'app per Windows 11. " +
        "Rispondi in italiano, in modo diretto e concreto, senza cercare sul web. " +
        "Per la matematica mostra il risultato e, se serve, i passaggi brevi. " +
        "Per Windows 11 indica il nome esatto dell'impostazione o il percorso " +
        "(ad esempio Impostazioni > Sistema > Schermo); se l'utente chiede " +
        "un'azione che la barra può eseguire, spiega in una riga cosa fare nella " +
        "barra. Non inventare funzioni di Nebula che non conosci e non chiedere " +
        "dati personali.";

    private static readonly (string[] Keys, string Uri, string Name)[] SettingsPages =
    {
        (new[] { "schermo", "display", "monitor" }, "ms-settings:display", "Schermo"),
        (new[] { "audio", "suono", "altoparlanti" }, "ms-settings:sound", "Audio"),
        (new[] { "wifi", "wi-fi", "reti wireless" }, "ms-settings:network-wifi", "Wi-Fi"),
        (new[] { "bluetooth" }, "ms-settings:bluetooth", "Bluetooth"),
        (new[] { "app installate", "disinstalla", "programmi installati" }, "ms-settings:appsfeatures", "App installate"),
        (new[] { "aggiornamenti", "update", "windows update" }, "ms-settings:windowsupdate", "Windows Update"),
        (new[] { "privacy", "permessi" }, "ms-settings:privacy", "Privacy"),
        (new[] { "data e ora", "fuso orario" }, "ms-settings:dateandtime", "Data e ora"),
        (new[] { "mouse", "touchpad" }, "ms-settings:mousetouchpad", "Mouse"),
        (new[] { "tastiera" }, "ms-settings:easeofaccess-keyboard", "Tastiera"),
        (new[] { "batteria", "risparmio energetico" }, "ms-settings:batterysaver", "Batteria"),
        (new[] { "archiviazione", "spazio su disco" }, "ms-settings:storagesense", "Archiviazione"),
        (new[] { "app predefinite" }, "ms-settings:defaultapps", "App predefinite"),
        (new[] { "personalizzazione", "sfondo", "colori" }, "ms-settings:personalization", "Personalizzazione"),
        (new[] { "taskbar di windows", "barra delle applicazioni" }, "ms-settings:taskbar", "Taskbar di Windows"),
        (new[] { "notifiche" }, "ms-settings:notifications", "Notifiche"),
        (new[] { "lingua", "regione" }, "ms-settings:regionlanguage", "Lingua e area"),
        (new[] { "account", "accesso" }, "ms-settings:yourinfo", "Account"),
        (new[] { "informazioni su windows", "versione di windows" }, "ms-settings:about", "Informazioni su Windows"),
        (new[] { "multitasking" }, "ms-settings:multitasking", "Multitasking")
    };

    /// <summary>
    /// Il suggerimento locale per la query, se c'è: calcolo, comando o pagina di
    /// Windows. Null quando la query è "solo" una ricerca di app o finestre.
    /// </summary>
    public static SmartSuggestion? Suggest(string query, AppCatalog? catalog = null)
    {
        string raw = (query ?? "").Trim();
        if (raw.Length < 2)
            return null;

        if (TryEvaluate(raw, out double value, out string formatted))
        {
            return new SmartSuggestion(
                SmartSuggestionKind.Math,
                "= " + formatted,
                "Calcolo · Invio copia il risultato",
                "\uE94E",
                CopyText: formatted);
        }

        string q = Normalize(raw);
        if (q.Length < 2)
            return null;

        // ms-settings: esplicito, prima di ogni altra regola.
        Match direct = Regex.Match(raw, @"ms-settings:[\w-]+", RegexOptions.IgnoreCase);
        if (direct.Success)
            return WindowsSettings(direct.Value, "Impostazioni di Windows");

        SmartSuggestion? page = TryWindowsSettings(q);
        if (page is not null)
            return page;

        SmartSuggestion? command = TryCommand(raw, q);
        if (command is not null)
            return command;

        SmartSuggestion? quick = TryQuickAction(q);
        if (quick is not null)
            return quick;

        SmartSuggestion? app = TryOpen(raw, q, catalog);
        return app;
    }

    /// <summary>True per una domanda che merita l'assistente, non per un comando.</summary>
    public static bool LooksLikeQuestion(string query)
    {
        string q = Normalize(query).Trim();
        if (q.Length < 3)
            return false;

        if (q.EndsWith('?'))
            return true;

        string[] starts =
        {
            "come ", "cosa ", "che cosa", "perche", "chi ", "quando", "dove ",
            "quanto ", "spiega", "mi spieghi", "aiutami", "puoi ", "secondo te",
            "scrivi", "riassumi", "traduci", "consiglia", "meglio ", "differenza "
        };

        return starts.Any(start => q.StartsWith(start, StringComparison.Ordinal));
    }

    public static bool IsAiConfigured(AppSettings settings) =>
        settings.SmartAiEnabled &&
        !string.IsNullOrWhiteSpace(settings.SmartAiEndpoint) &&
        !string.IsNullOrWhiteSpace(settings.SmartAiModel) &&
        !string.IsNullOrWhiteSpace(settings.SmartAiKey);

    /// <summary>
    /// Chiede una risposta al modello. Non lancia eccezioni: un endpoint spento,
    /// una chiave sbagliata o una risposta inattesa diventano un messaggio
    /// leggibile nella barra.
    /// </summary>
    public static async Task<AiReply> AskAiAsync(AppSettings settings, string question,
        CancellationToken cancellationToken, HttpMessageHandler? handler = null)
    {
        string endpoint = (settings.SmartAiEndpoint ?? "").Trim();
        string model = (settings.SmartAiModel ?? "").Trim();
        string key = (settings.SmartAiKey ?? "").Trim();

        if (endpoint.Length == 0 || model.Length == 0 || key.Length == 0)
            return new AiReply(null, "Configura endpoint, modello e chiave in Studio → Ricerca.");

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? baseUri) ||
            baseUri.Scheme is not ("http" or "https"))
            return new AiReply(null, "L'endpoint AI non è un URL valido.");

        Uri target = BuildChatUri(baseUri);
        var payload = new
        {
            model,
            temperature = 0.3,
            max_tokens = 600,
            messages = new object[]
            {
                new { role = "system", content = SystemPrompt },
                new { role = "user", content = question }
            }
        };

        string json = JsonSerializer.Serialize(payload);
        using var request = new HttpRequestMessage(HttpMethod.Post, target)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        // La chiave viaggia solo nell'header e non finisce mai nei log.
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");

        HttpClient client = SharedHttp;
        HttpClient? owned = null;
        if (handler is not null)
        {
            owned = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            client = owned;
        }

        try
        {
            using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
            string body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return new AiReply(null,
                    $"Il servizio AI ha risposto {(int)response.StatusCode}. " +
                    "Controlla endpoint, modello e chiave in Studio → Ricerca.");
            }

            return ParseAnswer(body);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            return new AiReply(null, "Il servizio AI non ha risposto in tempo.");
        }
        catch (Exception ex)
        {
            Log.Write("Smart AI request failed: " + ex.GetType().Name);
            return new AiReply(null, "Non riesco a raggiungere il servizio AI. Controlla la connessione e l'endpoint.");
        }
        finally
        {
            owned?.Dispose();
        }
    }

    private static Uri BuildChatUri(Uri baseUri)
    {
        string text = baseUri.ToString().TrimEnd('/');
        if (text.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            return new Uri(text, UriKind.Absolute);

        return new Uri(text + "/chat/completions", UriKind.Absolute);
    }

    private static AiReply ParseAnswer(string body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;

            if (root.TryGetProperty("error", out JsonElement error))
            {
                string? message = error.ValueKind == JsonValueKind.Object &&
                                  error.TryGetProperty("message", out JsonElement text)
                    ? text.GetString()
                    : null;
                return new AiReply(null,
                    string.IsNullOrWhiteSpace(message)
                        ? "Il servizio AI ha restituito un errore."
                        : message);
            }

            if (root.TryGetProperty("choices", out JsonElement choices) &&
                choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
            {
                JsonElement first = choices[0];
                if (first.TryGetProperty("message", out JsonElement message) &&
                    message.TryGetProperty("content", out JsonElement content) &&
                    content.ValueKind == JsonValueKind.String)
                {
                    string? answer = content.GetString()?.Trim();
                    return string.IsNullOrWhiteSpace(answer)
                        ? new AiReply(null, "L'assistente non ha restituito testo.")
                        : new AiReply(answer, null);
                }

                if (first.TryGetProperty("text", out JsonElement text) &&
                    text.ValueKind == JsonValueKind.String)
                {
                    string? answer = text.GetString()?.Trim();
                    return string.IsNullOrWhiteSpace(answer)
                        ? new AiReply(null, "L'assistente non ha restituito testo.")
                        : new AiReply(answer, null);
                }
            }

            return new AiReply(null, "Risposta AI non riconosciuta: controlla il modello.");
        }
        catch (JsonException)
        {
            return new AiReply(null, "Il servizio AI ha restituito una risposta non valida.");
        }
    }

    // ---------------------------------------------------------------- comandi

    private static SmartSuggestion? TryWindowsSettings(string q)
    {
        bool asksSettings = q.Contains("impostazion") || q.Contains("impostazioni") ||
                            q.Contains("pannello") || q.Contains("apri ") ||
                            q.Contains("pagina ");
        if (!asksSettings)
            return null;

        foreach ((string[] keys, string uri, string name) in SettingsPages)
        {
            if (keys.Any(key => q.Contains(key)))
                return WindowsSettings(uri, name);
        }

        return null;
    }

    private static SmartSuggestion WindowsSettings(string uri, string name) =>
        new(SmartSuggestionKind.Command,
            "Apri impostazioni di Windows: " + name,
            "Invio apre " + uri,
            "\uE770",
            new SmartCommand("windows.settings", uri));

    private static SmartSuggestion? TryCommand(string raw, string q)
    {
        // Tema: un preset per nome, oppure chiaro/scuro.
        if (q.Contains("tema") || q.Contains("modalita scura") || q.Contains("modalita chiara"))
        {
            ThemePreset? preset = ThemePreset.All.FirstOrDefault(candidate =>
                q.Contains(Normalize(candidate.Name)));

            if (preset is null)
            {
                if (q.Contains("scur")) preset = ThemePreset.All.FirstOrDefault(p => !p.IsLight);
                else if (q.Contains("chiar")) preset = ThemePreset.All.FirstOrDefault(p => p.IsLight);
            }

            if (preset is not null)
            {
                return new SmartSuggestion(SmartSuggestionKind.Command,
                    $"Tema {preset.Name}", "Invio applica il tema a Nebula.",
                    "\uE790", new SmartCommand("theme.set", preset.Id));
            }
        }

        // Dove vive la dock.
        if (q.Contains("dock") || q.Contains("barra"))
        {
            if (q.Contains("in alto"))
            {
                return new SmartSuggestion(SmartSuggestionKind.Command,
                    "Dock in alto", "Invio sposta la barra sul bordo superiore.",
                    "\uE74A", new SmartCommand("dock.top"));
            }

            if (q.Contains("in basso"))
            {
                return new SmartSuggestion(SmartSuggestionKind.Command,
                    "Dock in basso", "Invio riporta la barra sul bordo inferiore.",
                    "\uE74B", new SmartCommand("dock.bottom"));
            }
        }

        if (q.Contains("auto-hide") || q.Contains("autohide") ||
            q.Contains("nascondi automaticamente") || q.Contains("nasconditi"))
        {
            bool off = q.Contains("disattiva") || q.Contains("togli") || q.Contains("non ");
            return new SmartSuggestion(SmartSuggestionKind.Command,
                off ? "Disattiva l'auto-hide" : "Attiva l'auto-hide",
                off ? "Invio lascia la dock sempre visibile." : "Invio ritrae la dock quando non serve.",
                "\uE7B3", new SmartCommand(off ? "dock.autohide.off" : "dock.autohide.on"));
        }

        if (q.Contains("taskbar") &&
            (q.Contains("ripristina") || q.Contains("originale") ||
             (q.Contains("mostra") && q.Contains("windows"))))
        {
            return new SmartSuggestion(SmartSuggestionKind.Command,
                "Ripristina la taskbar di Windows",
                "Invio mostra di nuovo la barra nativa e spegne la sostituzione.",
                "\uE7B3", new SmartCommand("dock.replace.off"));
        }

        if (q.Contains("taskbar") &&
            (q.Contains("nascondi") || q.Contains("sostituisci") || q.Contains("togli")))
        {
            return new SmartSuggestion(SmartSuggestionKind.Command,
                "Nascondi la taskbar di Windows",
                "Invio attiva la sostituzione con la barra di Nebula.",
                "\uE7B3", new SmartCommand("dock.replace.on"));
        }

        if (q.Contains("a tutta larghezza"))
        {
            return new SmartSuggestion(SmartSuggestionKind.Command,
                "Barra a tutta larghezza", "Invio estende la dock da un bordo all'altro.",
                "\uE740", new SmartCommand("dock.width.on"));
        }

        if (q.Contains("compatta") && (q.Contains("barra") || q.Contains("dock")))
        {
            return new SmartSuggestion(SmartSuggestionKind.Command,
                "Barra compatta", "Invio toglie la larghezza completa alla dock.",
                "\uE740", new SmartCommand("dock.width.off"));
        }

        if (q.Contains("icon"))
        {
            Match size = Regex.Match(q, @"icon[^0-9]{0,20}(\d{2})");
            if (size.Success && int.TryParse(size.Groups[1].Value, out int iconSize))
            {
                return new SmartSuggestion(SmartSuggestionKind.Command,
                    $"Icone da {iconSize} px", "Invio imposta la dimensione delle icone.",
                    "\uE8FD", new SmartCommand("icons.set", iconSize.ToString(CultureInfo.InvariantCulture)));
            }

            if (q.Contains("grand") || q.Contains("aumenta") || q.Contains("ingrandisci"))
            {
                return new SmartSuggestion(SmartSuggestionKind.Command,
                    "Icone più grandi", "Invio aumenta le icone della dock.",
                    "\uE8FD", new SmartCommand("icons.plus"));
            }

            if (q.Contains("piccol") || q.Contains("riduci") || q.Contains("diminuisci"))
            {
                return new SmartSuggestion(SmartSuggestionKind.Command,
                    "Icone più piccole", "Invio riduce le icone della dock.",
                    "\uE8FD", new SmartCommand("icons.minus"));
            }
        }

        if (q.Contains("barra") || q.Contains("dock"))
        {
            Match height = Regex.Match(q, @"altezza[^0-9]{0,24}(\d{2})");
            if (height.Success && int.TryParse(height.Groups[1].Value, out int barHeight))
            {
                return new SmartSuggestion(SmartSuggestionKind.Command,
                    $"Altezza barra {barHeight} px", "Invio imposta l'altezza della dock.",
                    "\uE70E", new SmartCommand("dock.height.set", barHeight.ToString(CultureInfo.InvariantCulture)));
            }

            if (q.Contains("alta") || q.Contains("spessa") || q.Contains("aumenta"))
            {
                return new SmartSuggestion(SmartSuggestionKind.Command,
                    "Barra più alta", "Invio aumenta l'altezza della dock.",
                    "\uE70E", new SmartCommand("dock.height.plus"));
            }

            if (q.Contains("bassa") || q.Contains("sottile") || q.Contains("riduci"))
            {
                return new SmartSuggestion(SmartSuggestionKind.Command,
                    "Barra più bassa", "Invio riduce l'altezza della dock.",
                    "\uE70E", new SmartCommand("dock.height.minus"));
            }
        }

        if (q.Contains("schede") &&
            (q.Contains("attiva") || q.Contains("disattiva") || q.Contains("abilita") || q.Contains("disabilita")))
        {
            bool off = q.Contains("disattiva") || q.Contains("disabilita");
            return new SmartSuggestion(SmartSuggestionKind.Command,
                off ? "Disattiva le schede" : "Attiva le schede",
                off ? "Invio smette di raggruppare le finestre in schede." :
                      "Invio abilita la tab bar di Nebula.",
                "\uE7C4", new SmartCommand(off ? "tabs.off" : "tabs.on"));
        }

        SmartSuggestion? volume = TryVolume(q);
        if (volume is not null)
            return volume;

        SmartSuggestion? brightness = TryBrightness(q);
        if (brightness is not null)
            return brightness;

        return TryPower(q);
    }

    private static SmartSuggestion? TryVolume(string q)
    {
        if (!q.Contains("volume") && !q.Contains("audio") && !q.Contains("muto"))
            return null;

        Match set = Regex.Match(q, @"volume[^0-9]{0,14}(\d{1,3})");
        if (set.Success && int.TryParse(set.Groups[1].Value, out int percent))
        {
            return new SmartSuggestion(SmartSuggestionKind.Command,
                $"Volume al {Math.Clamp(percent, 0, 100)}%", "Invio imposta il volume di Windows.",
                "\uE767", new SmartCommand("volume.set", Math.Clamp(percent, 0, 100).ToString(CultureInfo.InvariantCulture)));
        }

        if (q.Contains("riattiva") || q.Contains("togli muto") || q.Contains("disattiva muto"))
        {
            return new SmartSuggestion(SmartSuggestionKind.Command,
                "Riattiva l'audio", "Invio toglie il muto.",
                "\uE767", new SmartCommand("volume.unmute"));
        }

        if (q.Contains("muto") || q.Contains("mute"))
        {
            return new SmartSuggestion(SmartSuggestionKind.Command,
                "Disattiva l'audio", "Invio mette il volume in muto.",
                "\uE74F", new SmartCommand("volume.mute"));
        }

        if (q.Contains("aumenta") || q.Contains("alza") || q.Contains("su"))
        {
            return new SmartSuggestion(SmartSuggestionKind.Command,
                "Alza il volume", "Invio aumenta il volume di 5 punti.",
                "\uE767", new SmartCommand("volume.up"));
        }

        if (q.Contains("abbassa") || q.Contains("riduci") || q.Contains("giu"))
        {
            return new SmartSuggestion(SmartSuggestionKind.Command,
                "Abbassa il volume", "Invio riduce il volume di 5 punti.",
                "\uE767", new SmartCommand("volume.down"));
        }

        return null;
    }

    private static SmartSuggestion? TryBrightness(string q)
    {
        if (!q.Contains("luminos"))
            return null;

        Match set = Regex.Match(q, @"luminos[^0-9]{0,20}(\d{1,3})");
        if (set.Success && int.TryParse(set.Groups[1].Value, out int percent))
        {
            return new SmartSuggestion(SmartSuggestionKind.Command,
                $"Luminosità al {Math.Clamp(percent, 0, 100)}%", "Invio imposta la luminosità del monitor.",
                "\uE706", new SmartCommand("brightness.set", Math.Clamp(percent, 0, 100).ToString(CultureInfo.InvariantCulture)));
        }

        if (q.Contains("aumenta") || q.Contains("alza"))
        {
            return new SmartSuggestion(SmartSuggestionKind.Command,
                "Più luminosità", "Invio aumenta la luminosità di 10 punti.",
                "\uE706", new SmartCommand("brightness.up"));
        }

        if (q.Contains("abbassa") || q.Contains("riduci"))
        {
            return new SmartSuggestion(SmartSuggestionKind.Command,
                "Meno luminosità", "Invio riduce la luminosità di 10 punti.",
                "\uE706", new SmartCommand("brightness.down"));
        }

        return null;
    }

    private static SmartSuggestion? TryPower(string q)
    {
        if (q.Contains("spegni") || q == "shutdown")
        {
            return new SmartSuggestion(SmartSuggestionKind.Command,
                "Spegni il PC", "Invio chiede conferma e spegne Windows.",
                "\uE7E8", new SmartCommand("power", "shutdown", Confirm: true));
        }

        if (q.Contains("riavvia"))
        {
            return new SmartSuggestion(SmartSuggestionKind.Command,
                "Riavvia il PC", "Invio chiede conferma e riavvia Windows.",
                "\uE777", new SmartCommand("power", "restart", Confirm: true));
        }

        if (q.Contains("sospendi") || q.Contains("sleep"))
        {
            return new SmartSuggestion(SmartSuggestionKind.Command,
                "Sospendi il PC", "Invio mette il PC in sospensione.",
                "\uE708", new SmartCommand("power", "sleep"));
        }

        if (q.Contains("disconnetti") || q.Contains("esci da windows"))
        {
            return new SmartSuggestion(SmartSuggestionKind.Command,
                "Disconnetti l'utente", "Invio chiede conferma e chiude la sessione.",
                "\uE748", new SmartCommand("power", "signout", Confirm: true));
        }

        if (q == "blocca" || q.Contains("blocca pc") || q.Contains("blocca sessione") ||
            q.Contains("blocca il pc"))
        {
            return new SmartSuggestion(SmartSuggestionKind.Command,
                "Blocca il PC", "Invio blocca la sessione.",
                "\uE72E", new SmartCommand("power", "lock"));
        }

        return null;
    }

    private static SmartSuggestion? TryQuickAction(string q)
    {
        (string[] Keys, string Id, string Name)[] actions =
        {
            (new[] { "screenshot", "cattura schermo", "cattura" }, "screenshot", "Cattura schermo"),
            (new[] { "appunti" }, "clipboard", "Appunti"),
            (new[] { "emoji", "faccine" }, "emoji", "Emoji"),
            (new[] { "terminale", "powershell", "prompt dei comandi" }, "terminal", "Terminale"),
            (new[] { "gestione attivita", "task manager" }, "taskmgr", "Gestione attività"),
            (new[] { "esplora file", "explorer", "cartella personale" }, "explorer", "Esplora file"),
            (new[] { "cestino" }, "recycle", "Cestino"),
            (new[] { "notifiche" }, "notifications", "Notifiche"),
            (new[] { "impostazioni rapide" }, "quicksettings", "Impostazioni rapide"),
            (new[] { "panoramica", "task view", "tutte le finestre" }, "taskview", "Panoramica"),
            (new[] { "mostra desktop" }, "showdesktop", "Mostra desktop"),
            (new[] { "play", "pausa", "riproduci", "musica" }, "media", "Play / pausa"),
            (new[] { "widget" }, "widgets", "Pannello widget"),
            (new[] { "controlli rapidi", "controlli" }, "controls", "Controlli rapidi"),
            (new[] { "schede", "raggruppa finestre" }, "tabs", "Schede finestre"),
            (new[] { "studio", "impostazioni di nebula" }, "settings", "Nebula Studio")
        };

        foreach ((string[] keys, string id, string name) in actions)
        {
            if (!keys.Any(key => q.Contains(key)))
                continue;

            // Il nome di un'app non deve diventare un comando solo perché
            // contiene una parola chiave ("Controlli" di un'app): i verbi
            // espliciti restano la via principale.
            bool explicitAsk = q.StartsWith("apri ") || q.StartsWith("lancia ") ||
                               q.StartsWith("avvia ") || q.StartsWith("mostra ") ||
                               q.Contains("pannello") || q.Contains("funzione");
            if (!explicitAsk && q.Length > keys.Max(key => key.Length) + 18)
                continue;

            return new SmartSuggestion(SmartSuggestionKind.Command,
                name, "Invio apre " + name + ".",
                "\uE8A7", new SmartCommand("quick", id));
        }

        return null;
    }

    private static SmartSuggestion? TryOpen(string raw, string q, AppCatalog? catalog)
    {
        if (catalog is null)
            return null;

        string target = q;
        foreach (string prefix in new[] { "apri ", "lancia ", "avvia ", "esegui " })
        {
            if (q.StartsWith(prefix, StringComparison.Ordinal))
            {
                target = q[prefix.Length..].Trim();
                break;
            }
        }

        if (target.Length < 2 || target == q && !q.StartsWith("apri "))
            return null;

        AppEntry? app = catalog.Search(target, 6)
            .FirstOrDefault(entry => entry.Kind == AppEntryKind.Application &&
                                     !string.IsNullOrWhiteSpace(entry.Path));
        if (app is null)
            return null;

        return new SmartSuggestion(SmartSuggestionKind.Command,
            "Apri " + app.Name,
            "Invio avvia " + app.Path,
            "\uE8A7", new SmartCommand("app.launch", app.Path));
    }

    // ------------------------------------------------------------- matematica

    public static bool TryEvaluate(string query, out double value, out string formatted)
    {
        value = 0;
        formatted = "";

        string text = (query ?? "").Trim().ToLowerInvariant()
            .Replace('×', '*').Replace('÷', '/').Replace('−', '-');
        if (text.Length == 0)
            return false;

        // "il 15% di 240", "15 % su 300"
        Match percent = Regex.Match(text,
            @"(-?\d+(?:[.,]\d+)?)\s*%\s*(?:di|del|della|dei|su)\s*(-?\d+(?:[.,]\d+)?)");
        if (percent.Success &&
            TryParseNumber(percent.Groups[1].Value, out double part) &&
            TryParseNumber(percent.Groups[2].Value, out double total))
        {
            value = total * part / 100.0;
            formatted = FormatNumber(value);
            return true;
        }

        foreach (string prefix in new[]
                 {
                     "quanto fa ", "quanto è ", "quanto e ", "quanto vale ", "calcola ",
                     "risultato di ", "risolvi ", "=", "?"
                 })
        {
            if (text.StartsWith(prefix, StringComparison.Ordinal))
                text = text[prefix.Length..].Trim();
        }

        text = text.TrimEnd('?', '=').Trim();
        if (text.Length < 1 || !Regex.IsMatch(text, @"^[0-9+\-*/^().,\s]+$"))
            return false;

        text = Regex.Replace(text, @"(?<=\d),(?=\d)", ".");
        try
        {
            var parser = new MathParser(text);
            value = parser.Parse();
            if (double.IsNaN(value) || double.IsInfinity(value))
                return false;

            formatted = FormatNumber(value);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryParseNumber(string text, out double number) =>
        double.TryParse(text.Replace(',', '.'), NumberStyles.Float,
            CultureInfo.InvariantCulture, out number);

    private static string FormatNumber(double value)
    {
        double rounded = Math.Round(value, 8);
        string text = rounded.ToString("0.########", Italian);
        return text == "-0" ? "0" : text;
    }

    private sealed class MathParser
    {
        private readonly string _text;
        private int _index;

        public MathParser(string text) => _text = text;

        public double Parse()
        {
            double value = Expression();
            SkipSpaces();
            if (_index < _text.Length)
                throw new FormatException("Carattere inatteso nel calcolo.");

            return value;
        }

        private double Expression()
        {
            double value = Term();
            while (true)
            {
                SkipSpaces();
                if (Take('+')) value += Term();
                else if (Take('-')) value -= Term();
                else return value;
            }
        }

        private double Term()
        {
            double value = Power();
            while (true)
            {
                SkipSpaces();
                if (Take('*'))
                {
                    value *= Power();
                }
                else if (Take('/'))
                {
                    double divisor = Power();
                    if (Math.Abs(divisor) < 1e-12)
                        throw new DivideByZeroException();

                    value /= divisor;
                }
                else
                {
                    return value;
                }
            }
        }

        private double Power()
        {
            double value = Unary();
            SkipSpaces();
            return Take('^') ? Math.Pow(value, Power()) : value;
        }

        private double Unary()
        {
            SkipSpaces();
            if (Take('-')) return -Unary();
            if (Take('+')) return Unary();
            return Primary();
        }

        private double Primary()
        {
            SkipSpaces();
            if (Take('('))
            {
                double value = Expression();
                SkipSpaces();
                Take(')');
                return value;
            }

            int start = _index;
            while (_index < _text.Length &&
                   (char.IsDigit(_text[_index]) || _text[_index] == '.'))
                _index++;

            if (start == _index)
                throw new FormatException("Numero atteso.");

            return double.Parse(_text[start.._index], CultureInfo.InvariantCulture);
        }

        private bool Take(char character)
        {
            if (_index >= _text.Length || _text[_index] != character)
                return false;

            _index++;
            return true;
        }

        private void SkipSpaces()
        {
            while (_index < _text.Length && char.IsWhiteSpace(_text[_index]))
                _index++;
        }
    }

    private static string Normalize(string text)
    {
        string lower = (text ?? "").Trim().ToLowerInvariant();
        var builder = new StringBuilder(lower.Length);

        foreach (char character in lower.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
                builder.Append(character);
        }

        return builder.ToString();
    }
}

/// <summary>Esito di una domanda all'assistente: testo oppure errore leggibile.</summary>
public sealed record AiReply(string? Text, string? Error);
