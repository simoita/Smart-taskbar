namespace Nebula.Models;

public enum SmartSuggestionKind
{
    /// <summary>Risultato di un calcolo, da mostrare e copiare.</summary>
    Math,
    /// <summary>Comando pronto: Invio lo esegue.</summary>
    Command,
    /// <summary>Risposta dell'assistente AI, da leggere e copiare.</summary>
    Answer
}

/// <summary>
/// Un comando che la barra può eseguire. <see cref="Confirm"/> chiede una
/// conferma prima di eseguirlo (spegnimento, riavvio, disconnessione).
/// </summary>
public sealed record SmartCommand(string Id, string? Argument = null, bool Confirm = false);

/// <summary>
/// Una riga intelligente sopra i risultati: un calcolo, un comando o una
/// risposta. Non è un risultato da aprire: la barra la esegue o la copia.
/// </summary>
public sealed record SmartSuggestion(
    SmartSuggestionKind Kind,
    string Title,
    string Detail,
    string Glyph,
    SmartCommand? Command = null,
    string? CopyText = null);
