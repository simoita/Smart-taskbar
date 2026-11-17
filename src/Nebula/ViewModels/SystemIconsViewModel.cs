using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Nebula.Models;
using Nebula.Services;

namespace Nebula.ViewModels;

public sealed record SystemIconStyleOption(string Id, string Name, string Subtitle);
public sealed record SystemIconShapeOption(string Id, string Name);
public sealed record SystemIconScopeOption(string Id, string Name);

public sealed class SystemIconItemViewModel : ObservableObject
{
    private bool _isApplied;
    private string _appliedStyle = "";
    private string _statusText = "Originale";

    public SystemIconItemViewModel(SystemIconTarget target, SystemIconOperation? operation)
    {
        Target = target;
        ApplyOperation(operation);
    }

    public SystemIconTarget Target { get; }
    public string DisplayName => Target.DisplayName;
    public string Detail => Target.Detail;
    public string KindLabel => Target.KindLabel;
    public string Badge => Target.Kind switch
    {
        SystemIconTargetKind.Shortcut => "LNK",
        SystemIconTargetKind.FileType => "FILE",
        _ => "DIR"
    };
    public string Glyph => Target.Kind switch
    {
        SystemIconTargetKind.Shortcut => "\uE71B",
        SystemIconTargetKind.FileType => "\uE8A5",
        _ => "\uE8B7"
    };
    public string SourceText => string.IsNullOrWhiteSpace(Target.SourceIcon)
        ? "Icona predefinita di Windows"
        : Path.GetFileName(Target.SourceIcon);

    public bool IsApplied
    {
        get => _isApplied;
        private set => SetProperty(ref _isApplied, value);
    }

    public string AppliedStyle
    {
        get => _appliedStyle;
        private set => SetProperty(ref _appliedStyle, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string ActionText => IsApplied ? "Cambia" : "Scegli icona";

    public void ApplyOperation(SystemIconOperation? operation)
    {
        if (operation is null)
        {
            IsApplied = false;
            AppliedStyle = "";
            StatusText = "Originale";
        }
        else
        {
            IsApplied = true;
            AppliedStyle = string.IsNullOrWhiteSpace(operation.Style) ? "personalizzata" : operation.Style;
            StatusText = "Personalizzata · " + AppliedStyle;
        }
        OnPropertyChanged(nameof(ActionText));
    }
}

public sealed class SystemIconsViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly SystemIconService _icons;
    private readonly List<SystemIconItemViewModel> _allItems = new();
    private string _status = "Scegli uno stile e applica le icone a livello utente. Ogni modifica ha un backup reversibile.";
    private string _selectedFilter = "all";
    private string _searchText = "";
    private bool _busy;

    public SystemIconsViewModel(AppSettings settings, SystemIconService icons)
    {
        _settings = settings;
        _icons = icons;

        StyleOptions = new ObservableCollection<SystemIconStyleOption>
        {
            new("native", "Originale", "Nessuna modifica"),
            new("glass", "Vetro", "Lastra scura e bordo luminoso"),
            new("filled", "Pieno", "Piastra color accento"),
            new("tile", "Riquadro", "Piastra compatta adattiva"),
            new("circle", "Cerchio", "Piastra circolare"),
            new("duotone", "Duotono", "Luce accento + ombre"),
            new("mono", "Mono", "Scala di grigi pulita"),
            new("outline", "Contorno", "Sagoma luminosa"),
            new("neon", "Neon", "Glow e bordo acceso")
        };

        ShapeOptions = new ObservableCollection<SystemIconShapeOption>
        {
            new("rounded", "Arrotondata"),
            new("squircle", "Squircle"),
            new("circle", "Cerchio"),
            new("square", "Quadrata")
        };

        FilterOptions = new ObservableCollection<SystemIconScopeOption>
        {
            new("all", "Tutte"),
            new("shortcut", "Collegamenti"),
            new("file", "Tipi file"),
            new("folder", "Cartelle")
        };

        if (!StyleOptions.Any(o => o.Id == settings.SystemIconStyle))
            settings.SystemIconStyle = "filled";
        if (!ShapeOptions.Any(o => o.Id == settings.SystemIconShape))
            settings.SystemIconShape = "rounded";
    }

    public ObservableCollection<SystemIconStyleOption> StyleOptions { get; }
    public ObservableCollection<SystemIconShapeOption> ShapeOptions { get; }
    public ObservableCollection<SystemIconScopeOption> FilterOptions { get; }
    public ObservableCollection<SystemIconItemViewModel> Items { get; } = new();

    public SystemIconService Service => _icons;

    public string SelectedStyle
    {
        get => _settings.SystemIconStyle;
        set
        {
            if (string.IsNullOrWhiteSpace(value) || _settings.SystemIconStyle == value)
                return;
            _settings.SystemIconStyle = value;
            OnPropertyChanged();
        }
    }

    public string SelectedShape
    {
        get => _settings.SystemIconShape;
        set
        {
            if (string.IsNullOrWhiteSpace(value) || _settings.SystemIconShape == value)
                return;
            _settings.SystemIconShape = value;
            OnPropertyChanged();
        }
    }

    public string SelectedFilter
    {
        get => _selectedFilter;
        set
        {
            if (!SetProperty(ref _selectedFilter, value))
                return;
            ApplyFilter();
        }
    }

    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value))
                return;
            ApplyFilter();
        }
    }

    public bool Busy
    {
        get => _busy;
        set
        {
            if (SetProperty(ref _busy, value))
            {
                OnPropertyChanged(nameof(IsIdle));
                OnPropertyChanged(nameof(BusyVisibility));
            }
        }
    }

    public bool IsIdle => !_busy;
    public System.Windows.Visibility BusyVisibility =>
        _busy ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    public string CountText => $"{_allItems.Count} superfici · {Items.Count} visibili";
    public string BackupText => _icons.HasBackup
        ? "Backup attivo · ripristino verificato disponibile"
        : "Nessun backup: le icone sono originali";
    public string AppliedText => _icons.AppliedCount == 0
        ? "Nessuna icona personalizzata"
        : $"{_icons.AppliedCount} icone personalizzate";

    public event Action? Changed;

    public void Refresh()
    {
        try
        {
            SystemIconBackup? backup = _icons.GetBackup();
            Dictionary<string, SystemIconOperation> operations = (backup?.Operations ?? new List<SystemIconOperation>())
                .ToDictionary(o => o.Id, StringComparer.OrdinalIgnoreCase);

            _allItems.Clear();
            foreach (SystemIconTarget target in _icons.DiscoverTargets())
            {
                operations.TryGetValue(target.Id, out SystemIconOperation? operation);
                _allItems.Add(new SystemIconItemViewModel(target, operation));
            }

            ApplyFilter();
            OnPropertyChanged(nameof(BackupText));
            OnPropertyChanged(nameof(AppliedText));
            Status = _icons.HasBackup
                ? "Backup pronto: " + _icons.BackupPath
                : "Nessuna modifica attiva. Applica uno stile per iniziare.";
        }
        catch (Exception ex)
        {
            Status = "Scansione non riuscita: " + ex.Message;
        }
    }

    public void SetStatus(string status)
    {
        Status = status;
        OnPropertyChanged(nameof(BackupText));
        OnPropertyChanged(nameof(AppliedText));
        Changed?.Invoke();
    }

    public async Task ApplyAllAsync(string style, string shape)
    {
        List<SystemIconTarget> targets = _allItems.Select(i => i.Target).ToList();
        await ApplyTargetsAsync(targets, new SystemIconChoice(style, shape, null));
    }

    public async Task ApplyTargetAsync(SystemIconTarget target, SystemIconChoice choice)
    {
        await ApplyTargetsAsync(new[] { target }, choice);
    }

    private async Task ApplyTargetsAsync(IReadOnlyList<SystemIconTarget> targets, SystemIconChoice choice)
    {
        if (Busy)
            return;

        Busy = true;
        try
        {
            Status = "Preparo il backup delle icone originali…";
            SystemIconApplyOutcome outcome = await _icons.ApplyAsync(
                targets, choice, message => Status = message).ConfigureAwait(true);
            Status = outcome.Message;
            Refresh();
            SetStatus(outcome.Message);
        }
        finally
        {
            Busy = false;
        }
    }

    public async Task RestoreAsync(SystemIconTarget? only = null)
    {
        if (Busy)
            return;

        Busy = true;
        try
        {
            Status = "Ripristino le icone originali…";
            IReadOnlyList<SystemIconTarget>? targets = only is null
                ? null
                : new[] { only };
            SystemIconApplyOutcome outcome = await _icons.RestoreAsync(
                targets, message => Status = message).ConfigureAwait(true);
            Refresh();
            SetStatus(outcome.Message);
        }
        finally
        {
            Busy = false;
        }
    }

    private void ApplyFilter()
    {
        Items.Clear();
        foreach (SystemIconItemViewModel item in _allItems)
        {
            bool visible = _selectedFilter switch
            {
                "shortcut" => item.Target.Kind == SystemIconTargetKind.Shortcut,
                "file" => item.Target.Kind == SystemIconTargetKind.FileType,
                "folder" => item.Target.Kind == SystemIconTargetKind.Folder,
                _ => true
            };
            if (!visible)
                continue;
            if (_searchText.Length > 0 &&
                !item.DisplayName.Contains(_searchText, StringComparison.OrdinalIgnoreCase) &&
                !item.Detail.Contains(_searchText, StringComparison.OrdinalIgnoreCase) &&
                !item.SourceText.Contains(_searchText, StringComparison.OrdinalIgnoreCase))
                continue;
            Items.Add(item);
        }
        OnPropertyChanged(nameof(CountText));
    }
}
