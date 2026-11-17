using System.Collections.ObjectModel;
using System.Windows.Media;
using Nebula.Models;
using Nebula.Services;

namespace Nebula.ViewModels;

public sealed class TabChipViewModel : ObservableObject
{
    public TabChipViewModel(TabMember member, string styleKey)
    {
        Member = member;
        _styleKey = styleKey;
    }

    public TabMember Member { get; }
    public IntPtr Handle => Member.Handle;
    public string Title => Member.Title;
    public string ProcessName => System.IO.Path.GetFileNameWithoutExtension(Member.Capture.ExePath);
    public ImageSource? Icon => Member.Icon;

    private string _styleKey;
    public string StyleKey
    {
        get => _styleKey;
        set => SetProperty(ref _styleKey, value);
    }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Icon));
        OnPropertyChanged(nameof(ProcessName));
    }
}

public sealed class TabBarViewModel : ObservableObject
{
    private readonly TabGroup _group;
    private readonly TabGroupService _service;
    private readonly AppSettings _settings;

    public TabBarViewModel(TabGroup group, TabGroupService service, AppSettings settings)
    {
        _group = group;
        _service = service;
        _settings = settings;
        Sync();
    }

    public TabGroup Group => _group;
    public ObservableCollection<TabChipViewModel> Tabs { get; } = new();

    public string StyleKey => _settings.TabsStyle;
    public double BarHeight => Math.Clamp(_settings.TabsBarHeight, 34, 64);
    public string BarPosition => _settings.TabsBarPosition;
    public bool Animations => _settings.AnimationsEnabled;
    public double AnimationSpeed => Math.Clamp(_settings.AnimationSpeed, 0.5, 2);

    public void Sync()
    {
        var live = _group.Members.ToList();

        for (int i = Tabs.Count - 1; i >= 0; i--)
        {
            if (!live.Any(m => m.Handle == Tabs[i].Handle))
                Tabs.RemoveAt(i);
        }

        foreach (TabMember member in live)
        {
            TabChipViewModel? chip = Tabs.FirstOrDefault(t => t.Handle == member.Handle);
            if (chip is null)
            {
                chip = new TabChipViewModel(member, StyleKey);
                Tabs.Add(chip);
            }
            else
            {
                chip.Refresh();
            }

            chip.StyleKey = StyleKey;
            chip.IsSelected = member.Handle == _group.ActiveHandle;
        }

        for (int i = 0; i < live.Count; i++)
        {
            TabChipViewModel? chip = Tabs.FirstOrDefault(t => t.Handle == live[i].Handle);
            if (chip is null)
                continue;

            int current = Tabs.IndexOf(chip);
            if (current != i)
                Tabs.Move(current, i);
        }

        OnPropertyChanged(nameof(StyleKey));
        OnPropertyChanged(nameof(BarHeight));
        OnPropertyChanged(nameof(BarPosition));
        OnPropertyChanged(nameof(Animations));
        OnPropertyChanged(nameof(AnimationSpeed));
    }

    public TabChipViewModel? Find(IntPtr handle) =>
        Tabs.FirstOrDefault(t => t.Handle == handle);

    public int IndexOf(IntPtr handle) =>
        Tabs.ToList().FindIndex(t => t.Handle == handle);

    public void Select(TabChipViewModel chip) => _service.Select(_group, chip.Member);

    public void Close(TabChipViewModel chip) => _service.CloseMember(_group, chip.Member);

    public void Detach(TabChipViewModel chip) => _service.Detach(_group, chip.Member, out _);

    public void Move(TabChipViewModel chip, int targetIndex) =>
        _service.Move(_group, chip.Member, targetIndex);

    public void CloseGroup() => _service.CloseGroup(_group);
}
