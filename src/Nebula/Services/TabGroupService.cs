using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Threading;
using Nebula.Interop;
using Nebula.Models;

namespace Nebula.Services;

public sealed class TabMember
{
    public TabMember(WindowCapture capture)
    {
        Capture = capture;
        Title = capture.Title;
    }

    public WindowCapture Capture { get; }
    public IntPtr Handle => Capture.Handle;
    public string Title { get; set; }
    public ImageSource? Icon => Capture.Icon;
}

public sealed class TabGroup
{
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public List<TabMember> Members { get; } = new();
    public IntPtr ActiveHandle { get; set; }
    public long CreatedTimestamp { get; } = Stopwatch.GetTimestamp();

    public TabMember? ActiveMember =>
        Members.FirstOrDefault(m => m.Handle == ActiveHandle) ?? Members.FirstOrDefault();
}

/// <summary>
/// Owns window tab groups. It hides all but the selected window, restores exact
/// native placements on detach/close, and maintains the crash-recovery file.
/// </summary>
public sealed class TabGroupService : IDisposable
{
    private readonly AppSettings _settings;
    private readonly List<TabGroup> _groups = new();
    private readonly DispatcherTimer _timer;
    private bool _disposed;

    public TabGroupService(AppSettings settings)
    {
        _settings = settings;
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(300)
        };
        _timer.Tick += (_, _) => Tick();
    }

    public IReadOnlyList<TabGroup> Groups => _groups;
    public bool IsEnabled => _settings.EnableTabs;

    /// <summary>Membership or lifecycle changed; the tab-bar windows must be synced.</summary>
    public event Action? GroupsChanged;

    /// <summary>Titles, selection or active-window geometry may have changed.</summary>
    public event Action<TabGroup>? GroupUpdated;

    public bool CreateGroup(IReadOnlyList<IntPtr> handles, out string? error)
    {
        error = null;

        if (!_settings.EnableTabs)
        {
            error = "Le schede sono disattivate. Attivale in Studio → Schede.";
            return false;
        }

        IntPtr[] wanted = handles
            .Where(h => h != IntPtr.Zero)
            .Distinct()
            .ToArray();

        if (wanted.Length < 2)
        {
            error = "Servono almeno due finestre per creare un gruppo di schede.";
            return false;
        }

        var captures = new List<WindowCapture>();
        foreach (IntPtr handle in wanted)
        {
            if (FindGroup(handle) is not null)
            {
                error = "Una delle finestre è già in un gruppo di schede.";
                return false;
            }

            if (!WindowAccess.TryCapture(handle, out WindowCapture? capture, out string reason) ||
                capture is null)
            {
                string title = WindowAccess.ReadText(handle);
                string label = string.IsNullOrWhiteSpace(title) ? "La finestra" : $"“{title.Trim()}”";
                error = $"{label} non può essere raggruppata: {reason}.";
                return false;
            }

            captures.Add(capture);
        }

        IntPtr foreground = NativeMethods.GetForegroundWindow();
        IntPtr active = captures.Any(c => c.Handle == foreground)
            ? foreground
            : captures[0].Handle;

        var group = new TabGroup { ActiveHandle = active };
        foreach (WindowCapture capture in captures)
            group.Members.Add(new TabMember(capture));

        _groups.Add(group);

        // Persist before hiding: if the process is killed during grouping the
        // watchdog still knows every original placement.
        PersistRecovery();

        foreach (TabMember member in group.Members)
        {
            if (member.Handle == active)
                WindowAccess.ShowAndActivate(member.Handle);
            else
                WindowAccess.Hide(member.Handle);
        }

        _timer.Start();
        Log.Write($"Tab group created with {group.Members.Count} windows.");
        GroupsChanged?.Invoke();
        GroupUpdated?.Invoke(group);
        return true;
    }

    public void Select(TabGroup group, TabMember member)
    {
        if (!group.Members.Contains(member) || !NativeMethods.IsWindow(member.Handle))
            return;

        foreach (TabMember other in group.Members)
        {
            if (other == member)
                continue;

            if (NativeMethods.IsWindowVisible(other.Handle) && !NativeMethods.IsIconic(other.Handle))
                WindowAccess.Hide(other.Handle);
        }

        group.ActiveHandle = member.Handle;
        WindowAccess.ShowAndActivate(member.Handle);
        Log.Write($"Tab selected: {member.Title}");
        GroupUpdated?.Invoke(group);
    }

    public void Move(TabGroup group, TabMember member, int targetIndex)
    {
        int current = group.Members.IndexOf(member);
        if (current < 0)
            return;

        targetIndex = Math.Clamp(targetIndex, 0, group.Members.Count - 1);
        if (current == targetIndex)
            return;

        group.Members.RemoveAt(current);
        group.Members.Insert(targetIndex, member);
        GroupUpdated?.Invoke(group);
    }

    public void Detach(TabGroup group, TabMember member, out string? error)
    {
        error = null;
        if (!group.Members.Contains(member))
            return;

        if (!WindowAccess.RestorePlacement(member.Capture))
        {
            error = $"Non è stato possibile ripristinare “{member.Title}”.";
            return;
        }

        bool wasActive = group.ActiveHandle == member.Handle;
        group.Members.Remove(member);

        if (group.Members.Count == 0)
        {
            RemoveGroup(group);
            return;
        }

        if (group.Members.Count == 1)
        {
            TabMember remaining = group.Members[0];
            WindowAccess.RestorePlacement(remaining.Capture);
            RemoveGroup(group);
            return;
        }

        if (wasActive)
            Select(group, group.Members[0]);
        else
            GroupUpdated?.Invoke(group);

        PersistRecovery();
        GroupsChanged?.Invoke();
        Log.Write($"Tab detached: {member.Title}");
    }

    public void CloseMember(TabGroup group, TabMember member)
    {
        if (!group.Members.Contains(member))
            return;

        Log.Write($"Tab close requested: {member.Title}");
        WindowAccess.Close(member.Handle);
    }

    public void CloseGroup(TabGroup group)
    {
        if (!_groups.Contains(group))
            return;

        var failed = new List<TabMember>();
        foreach (TabMember member in group.Members)
        {
            if (!WindowAccess.RestorePlacement(member.Capture))
                failed.Add(member);
        }

        _groups.Remove(group);
        PersistRecovery();
        if (failed.Count > 0)
            SaveFailed(failed);

        Log.Write($"Tab group closed; {group.Members.Count} window(s) restored.");
        GroupsChanged?.Invoke();
        StopTimerIfIdle();
    }

    public void CloseAll()
    {
        if (_groups.Count == 0)
        {
            TabRecovery.Delete();
            return;
        }

        var failed = new List<TabMember>();
        foreach (TabGroup group in _groups.ToList())
        {
            foreach (TabMember member in group.Members)
            {
                if (!WindowAccess.RestorePlacement(member.Capture))
                    failed.Add(member);
            }
        }

        int count = _groups.Sum(g => g.Members.Count);
        _groups.Clear();
        _timer.Stop();

        if (failed.Count > 0)
            SaveFailed(failed);
        else
            TabRecovery.Delete();

        Log.Write($"All tab groups closed; {count} window(s) restored.");
        GroupsChanged?.Invoke();
    }

    public void ApplyEnabled()
    {
        if (!_settings.EnableTabs)
            CloseAll();
    }

    public void ApplyAppearance()
    {
        foreach (TabGroup group in _groups)
            GroupUpdated?.Invoke(group);
    }

    public IReadOnlyList<WindowCandidate> GetCandidates()
    {
        return WindowAccess.EnumerateCandidates()
            .Where(c => FindGroup(c.Handle) is null)
            .ToList();
    }

    public TabGroup? FindGroup(IntPtr handle) =>
        _groups.FirstOrDefault(g => g.Members.Any(m => m.Handle == handle));

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _timer.Stop();
        _groups.Clear();
    }

    private void Tick()
    {
        if (_disposed)
            return;

        bool membershipChanged = false;

        foreach (TabGroup group in _groups.ToList())
        {
            List<TabMember> closed = group.Members
                .Where(m => !NativeMethods.IsWindow(m.Handle))
                .ToList();

            foreach (TabMember member in closed)
            {
                group.Members.Remove(member);
                membershipChanged = true;
                Log.Write($"Tab removed because the window closed: {member.Title}");
            }

            if (group.Members.Count == 0)
            {
                _groups.Remove(group);
                membershipChanged = true;
                continue;
            }

            if (group.Members.Count == 1)
            {
                WindowAccess.RestorePlacement(group.Members[0].Capture);
                _groups.Remove(group);
                membershipChanged = true;
                continue;
            }

            if (group.Members.All(m => m.Handle != group.ActiveHandle))
            {
                Select(group, group.Members[0]);
                membershipChanged = true;
                continue;
            }

            foreach (TabMember member in group.Members)
            {
                string title = WindowAccess.ReadText(member.Handle).Trim();
                if (!string.IsNullOrWhiteSpace(title) && !string.Equals(title, member.Title, StringComparison.Ordinal))
                    member.Title = title;
            }

            GroupUpdated?.Invoke(group);
        }

        if (membershipChanged)
        {
            PersistRecovery();
            GroupsChanged?.Invoke();
            StopTimerIfIdle();
        }
    }

    private void RemoveGroup(TabGroup group)
    {
        _groups.Remove(group);
        PersistRecovery();
        GroupsChanged?.Invoke();
        StopTimerIfIdle();
    }

    private void StopTimerIfIdle()
    {
        if (_groups.Count == 0)
            _timer.Stop();
    }

    private void PersistRecovery()
    {
        TabRecovery.Save(_groups
            .SelectMany(g => g.Members)
            .Select(m => new TabRecoveryWindow(m.Handle, m.Capture.Placement)));
    }

    private static void SaveFailed(IEnumerable<TabMember> failed)
    {
        TabRecovery.Save(failed
            .Select(m => new TabRecoveryWindow(m.Handle, m.Capture.Placement)));
    }
}
