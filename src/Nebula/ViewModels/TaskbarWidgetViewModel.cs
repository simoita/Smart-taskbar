using Nebula.Models;
using Nebula.Services;

namespace Nebula.ViewModels;

public enum TaskbarWidgetKind
{
    Clock,
    Metric,
    Text,
    Icon,
    Separator
}

/// <summary>
/// One entry of the taskbar strip. A single template renders every kind, so
/// users can freely reorder clock, metrics, shortcuts and separators.
/// </summary>
public sealed class TaskbarWidgetViewModel : ObservableObject
{
    private string _primaryText = "";
    private string _secondaryText = "";
    private string _label = "";
    private string _label2 = "";
    private string _value2Text = "";
    private double _value;
    private double _value2;
    private bool _hasSecondBar;

    public TaskbarWidgetViewModel(TaskbarWidgetDefinition definition)
    {
        Id = definition.Id;
        Name = definition.Name;
        Description = definition.Description;
        Glyph = definition.Glyph;

        Kind = definition.Id switch
        {
            "clock" => TaskbarWidgetKind.Clock,
            "cpu" or "ram" or "stats" => TaskbarWidgetKind.Metric,
            "battery" or "network" => TaskbarWidgetKind.Text,
            "separator" => TaskbarWidgetKind.Separator,
            _ => TaskbarWidgetKind.Icon
        };

        _label = definition.Id switch
        {
            "cpu" => "CPU",
            "ram" => "RAM",
            "stats" => "CPU",
            _ => ""
        };
        _label2 = "RAM";
        IsMetric = Kind == TaskbarWidgetKind.Metric;
        IsText = Kind == TaskbarWidgetKind.Text;
        IsIcon = Kind == TaskbarWidgetKind.Icon;
        IsClock = Kind == TaskbarWidgetKind.Clock;
        IsSeparator = Kind == TaskbarWidgetKind.Separator;
    }

    public string Id { get; }
    public string Name { get; }
    public string Description { get; }
    public string Glyph { get; }
    public TaskbarWidgetKind Kind { get; }
    public string KindKey => Kind.ToString();

    public bool IsClock { get; }
    public bool IsMetric { get; }
    public bool IsText { get; }
    public bool IsIcon { get; }
    public bool IsSeparator { get; }

    public string PrimaryText
    {
        get => _primaryText;
        set => SetProperty(ref _primaryText, value);
    }

    public string SecondaryText
    {
        get => _secondaryText;
        set
        {
            if (SetProperty(ref _secondaryText, value))
                OnPropertyChanged(nameof(HasSecondaryText));
        }
    }

    public bool HasSecondaryText => !string.IsNullOrWhiteSpace(_secondaryText);

    public string Label
    {
        get => _label;
        set => SetProperty(ref _label, value);
    }

    public string Label2
    {
        get => _label2;
        set => SetProperty(ref _label2, value);
    }

    public string Value2Text
    {
        get => _value2Text;
        set => SetProperty(ref _value2Text, value);
    }

    public double Value
    {
        get => _value;
        set => SetProperty(ref _value, value);
    }

    public double Value2
    {
        get => _value2;
        set => SetProperty(ref _value2, value);
    }

    public bool HasSecondBar
    {
        get => _hasSecondBar;
        set => SetProperty(ref _hasSecondBar, value);
    }

    public string Tooltip => Description;

    public void Apply(DockViewModel dock)
    {
        AppSettings settings = dock.Settings;

        switch (Id)
        {
            case "clock":
                PrimaryText = dock.TimeText;
                SecondaryText = settings.ShowDate ? dock.DateText : "";
                break;

            case "cpu":
                Label = "CPU";
                PrimaryText = dock.Monitor.CpuText;
                Value = dock.Monitor.Cpu;
                HasSecondBar = false;
                break;

            case "ram":
                Label = "RAM";
                PrimaryText = dock.Monitor.RamText;
                Value = dock.Monitor.Ram;
                HasSecondBar = false;
                break;

            case "stats":
                Label = "CPU";
                PrimaryText = dock.Monitor.CpuText;
                Value = dock.Monitor.Cpu;
                Label2 = "RAM";
                Value2Text = dock.Monitor.RamText;
                Value2 = dock.Monitor.Ram;
                HasSecondBar = true;
                break;

            case "battery":
                PrimaryText = dock.Monitor.HasBattery
                    ? dock.Monitor.BatteryText
                    : "AC";
                break;

            case "network":
                PrimaryText = dock.Monitor.NetworkText;
                break;
        }
    }
}
