using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using Nebula.Models;
using Nebula.Services;
using Nebula.Utilities;
using Nebula.ViewModels;

namespace Nebula.Views;

public sealed class GroupPickerItem : ObservableObject
{
    private bool _isSelected;

    public GroupPickerItem(WindowCandidate candidate, bool selected)
    {
        Candidate = candidate;
        _isSelected = selected;
    }

    public WindowCandidate Candidate { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

public partial class GroupPickerWindow : Window
{
    private readonly ObservableCollection<GroupPickerItem> _items = new();

    public GroupPickerWindow(IReadOnlyList<WindowCandidate> candidates, IEnumerable<IntPtr> preselected)
    {
        InitializeComponent();
        TitleBarTheme.Apply(this);

        var selected = new HashSet<IntPtr>(preselected);
        foreach (WindowCandidate candidate in candidates)
        {
            var item = new GroupPickerItem(candidate, selected.Contains(candidate.Handle));
            item.PropertyChanged += OnItemChanged;
            _items.Add(item);
        }

        WindowItems.ItemsSource = _items;
        UpdateStatus();
    }

    public IReadOnlyList<IntPtr> SelectedHandles =>
        _items.Where(i => i.IsSelected).Select(i => i.Candidate.Handle).ToList();

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GroupPickerItem.IsSelected))
            UpdateStatus();
    }

    private void UpdateStatus()
    {
        int count = _items.Count(i => i.IsSelected);
        GroupButton.IsEnabled = count >= 2;
        StatusText.Text = count == 0
            ? "Seleziona almeno due finestre."
            : count == 1
                ? "Seleziona ancora una finestra."
                : $"{count} finestre selezionate.";
    }

    private void OnGroupClick(object sender, RoutedEventArgs e)
    {
        if (_items.Count(i => i.IsSelected) < 2)
            return;

        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnHeaderDrag(object sender, MouseButtonEventArgs e) =>
        PanelDrag.MoveWindow(this, e);
}
