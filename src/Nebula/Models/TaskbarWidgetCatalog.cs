namespace Nebula.Models;

/// <summary>
/// One taskbar widget. Expandable widgets have a rich layout (values and
/// bars); the shortcut widgets are icons and only icons, so the Studio must
/// not offer them an "Espanso" mode they cannot honour.
/// </summary>
public sealed record TaskbarWidgetDefinition(
    string Id, string Name, string Description, string Glyph, bool Expandable = true);

/// <summary>
/// Widgets that can live inside the Nebula taskbar strip, in any order.
/// The catalog is also used to validate settings loaded from disk.
/// </summary>
public static class TaskbarWidgetCatalog
{
    public static IReadOnlyList<TaskbarWidgetDefinition> All { get; } = new List<TaskbarWidgetDefinition>
    {
        // Clock and system metrics.
        new("clock", "Orologio", "Ora e data, con secondi opzionali.", "\uE823"),
        new("date", "Data", "Giorno e data completi; clic per il calendario.", "\uE787"),
        new("cpu", "CPU", "Carico del processore con barra.", "\uE950"),
        new("ram", "RAM", "Memoria in uso con barra.", "\uE964"),
        new("stats", "CPU + RAM", "Due misuratori compatti affiancati.", "\uE9D9"),
        new("ramdetail", "Dettaglio RAM", "Memoria usata sul totale, in GB.", "\uE964"),
        new("disk", "Disco", "Spazio libero e utilizzo del disco di sistema.", "\uEDA2"),
        new("uptime", "Uptime", "Tempo dall'accensione del PC.", "\uE916"),
        new("battery", "Batteria", "Stato della batteria o alimentazione AC.", "\uE83F"),
        new("network", "Rete", "Stato della connessione e pannello Wi-Fi/Bluetooth.", "\uE774"),

        // Audio, video and display.
        new("volume", "Volume", "Volume di Windows; clic per attivare o disattivare l'audio.", "\uE767"),
        new("brightness", "Luminosità", "Luminosità del monitor principale.", "\uE706"),
        new("media", "Media", "Play e pausa del player attivo.", "\uE768"),

        // Nebula surfaces.
        new("search", "Ricerca", "Apre la ricerca globale.", "\uE721", Expandable: false),
        new("tabs", "Schede", "Raggruppa finestre in schede.", "\uE7C4", Expandable: false),
        new("widgets", "Pannello widget", "Apre calendario e widget.", "\uE71D", Expandable: false),
        new("controls", "Controlli rapidi", "Apre volume, media e rete.", "\uE9E9", Expandable: false),
        new("settings", "Impostazioni", "Apre Nebula Studio.", "\uE713", Expandable: false),

        // Windows shortcuts and system panels.
        new("notifications", "Notifiche", "Apre il centro notifiche di Windows.", "\uE7E7", Expandable: false),
        new("quicksettings", "Impostazioni rapide", "Apre rete, volume e luminosità di Windows.", "\uE9E9", Expandable: false),
        new("taskview", "Panoramica", "Mostra tutte le finestre aperte.", "\uE8A9", Expandable: false),
        new("showdesktop", "Mostra desktop", "Riduce a icona tutte le finestre.", "\uE73F", Expandable: false),
        new("screenshot", "Cattura schermo", "Avvia lo strumento di cattura di Windows.", "\uE722", Expandable: false),
        new("clipboard", "Appunti", "Apre la cronologia degli appunti di Windows.", "\uE77F", Expandable: false),
        new("emoji", "Emoji", "Apre la tastiera emoji di Windows.", "\uE76E", Expandable: false),
        new("lock", "Blocca", "Blocca la sessione di Windows.", "\uE72E", Expandable: false),
        new("wifi", "Wi-Fi", "Apre il pannello Nebula con le reti Wi-Fi.", "\uE701", Expandable: false),
        new("bluetooth", "Bluetooth", "Apre il pannello Nebula con i dispositivi Bluetooth.", "\uE702", Expandable: false),
        new("explorer", "Esplora file", "Apre la cartella personale in Esplora file.", "\uE8B7", Expandable: false),
        new("terminal", "Terminale", "Apre Windows Terminal o PowerShell.", "\uE756", Expandable: false),
        new("taskmgr", "Gestione attività", "Apre Gestione attività di Windows.", "\uE9D9", Expandable: false),
        new("recycle", "Cestino", "Apre il cestino di Windows.", "\uE74D", Expandable: false),

        new("separator", "Separatore", "Linea verticale tra due gruppi.", "\uE76F", Expandable: false)
    };

    public static TaskbarWidgetDefinition? Find(string? id) =>
        All.FirstOrDefault(widget => string.Equals(widget.Id, id, StringComparison.OrdinalIgnoreCase));

    public static bool IsKnown(string? id) => Find(id) is not null;

    /// <summary>Upper bound shared by settings validation and the Studio editor.</summary>
    public const int MaxWidgets = 40;
}
