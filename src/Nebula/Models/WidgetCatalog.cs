namespace Nebula.Models;

public sealed record WidgetDefinition(string Id, string Name, string Description, string Glyph);

public static class WidgetCatalog
{
    public static IReadOnlyList<WidgetDefinition> All { get; } = new List<WidgetDefinition>
    {
        new("clock", "Orologio", "Ora corrente con secondi opzionali", "\uE823"),
        new("date", "Data", "Giorno e data localizzati", "\uE787"),
        new("calendar", "Calendario", "Mese corrente e giorno di oggi", "\uE787"),
        new("stats", "Sistema", "CPU e RAM in tempo reale", "\uE950"),
        new("battery", "Batteria", "Stato e percentuale della batteria", "\uE83F"),
        new("network", "Rete", "Adattatore attivo e velocita", "\uE968"),
        new("uptime", "Uptime", "Tempo di attivita del sistema", "\uE823"),
        new("disk", "Disco", "Spazio libero e utilizzo dei volumi", "\uEDA2"),
        new("media", "Media", "Play, pausa e traccia del player attivo", "\uE768"),
        new("search", "Ricerca", "Apre il launcher di Nebula", "\uE721"),
        new("controls", "Controlli", "Apre il pannello dei controlli rapidi", "\uE9E9")
    };

    public static WidgetDefinition Find(string? id) =>
        All.FirstOrDefault(w => string.Equals(w.Id, id, StringComparison.OrdinalIgnoreCase)) ?? All[0];
}