using Nebula.Models;

namespace Nebula.ViewModels;

/// <summary>Un pulsante scelto dall'utente per la zona azioni della dockbar.</summary>
public sealed class DockButtonViewModel
{
    public DockButtonViewModel(TaskbarWidgetDefinition definition)
    {
        Id = definition.Id;
        Name = definition.Name;
        Glyph = definition.Glyph;
        Description = definition.Description;
    }

    public string Id { get; }
    public string Name { get; }
    public string Glyph { get; }
    public string Description { get; }
}
