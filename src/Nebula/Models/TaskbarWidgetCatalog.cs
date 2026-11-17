namespace Nebula.Models;

public sealed record TaskbarWidgetDefinition(string Id, string Name, string Description, string Glyph);

/// <summary>
/// Widgets that can live inside the Nebula taskbar strip, in any order.
/// The catalog is also used to validate settings loaded from disk.
/// </summary>
public static class TaskbarWidgetCatalog
{
    public static IReadOnlyList<TaskbarWidgetDefinition> All { get; } = new List<TaskbarWidgetDefinition>
    {
        new("search", "Ricerca", "Apre la ricerca globale.", "\uE721"),
        new("clock", "Orologio", "Ora e data, con secondi opzionali.", "\uE823"),
        new("cpu", "CPU", "Carico del processore con barra.", "\uE950"),
        new("ram", "RAM", "Memoria in uso con barra.", "\uE964"),
        new("stats", "CPU + RAM", "Due misuratori compatti affiancati.", "\uE9D9"),
        new("battery", "Batteria", "Stato della batteria o alimentazione AC.", "\uE83F"),
        new("network", "Rete", "Stato della connessione attiva.", "\uE774"),
        new("media", "Media", "Play e pausa del player attivo.", "\uE768"),
        new("tabs", "Schede", "Raggruppa finestre in schede.", "\uE7C4"),
        new("widgets", "Pannello widget", "Apre calendario e widget.", "\uE71D"),
        new("controls", "Controlli rapidi", "Apre volume, media e rete.", "\uE9E9"),
        new("settings", "Impostazioni", "Apre Nebula Studio.", "\uE713"),
        new("separator", "Separatore", "Linea verticale tra due gruppi.", "\uE76F")
    };

    public static TaskbarWidgetDefinition? Find(string? id) =>
        All.FirstOrDefault(widget => string.Equals(widget.Id, id, StringComparison.OrdinalIgnoreCase));

    public static bool IsKnown(string? id) => Find(id) is not null;
}
