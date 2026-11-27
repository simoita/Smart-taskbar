namespace Nebula.Models;

/// <summary>
/// Funzioni, pannelli e widget che possono diventare un pulsante della dockbar.
/// Nome, descrizione e glifo arrivano dal catalogo dei widget: quando nasce una
/// nuova azione c'è un solo posto da aggiornare, e la dock resta coerente con la
/// barra.
/// </summary>
public static class DockButtonCatalog
{
    private static readonly string[] ActionableIds =
    {
        "search", "tabs", "widgets", "controls", "settings",
        "media", "volume", "network", "wifi", "bluetooth",
        "notifications", "quicksettings", "taskview", "showdesktop",
        "screenshot", "clipboard", "emoji", "lock",
        "explorer", "terminal", "taskmgr", "recycle"
    };

    public static IReadOnlyList<TaskbarWidgetDefinition> All { get; } =
        ActionableIds
            .Select(id => TaskbarWidgetCatalog.Find(id))
            .Where(definition => definition is not null)
            .Select(definition => definition!)
            .ToList();

    public static TaskbarWidgetDefinition? Find(string? id) =>
        All.FirstOrDefault(definition =>
            string.Equals(definition.Id, id, StringComparison.OrdinalIgnoreCase));

    public static bool IsKnown(string? id) => Find(id) is not null;

    /// <summary>
    /// Pulsanti già pronti: le funzioni di tutti i giorni che la dockbar mostra
    /// senza doverle configurare.
    /// </summary>
    public static IReadOnlyList<string> Defaults { get; } = new[]
    {
        "showdesktop", "taskview", "screenshot", "clipboard", "lock", "recycle"
    };

    /// <summary>Tetto condiviso da salvataggio e Studio: la zona azioni resta leggibile.</summary>
    public const int MaxButtons = 12;
}
