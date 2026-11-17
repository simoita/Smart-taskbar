using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Nebula.Interop;
using Nebula.Models;
using Nebula.Services;
using Nebula.Utilities;
using Nebula.ViewModels;
using Nebula.Views;
using Forms = System.Windows.Forms;

namespace Nebula;

public partial class App
{
    private sealed record SelfTestCheck(string Name, bool Passed, string Detail);

    /// <summary>
    /// End-to-end proof that the replacement is real: it uses an isolated settings
    /// directory, registers a genuine AppBar, hides the native bars, measures the
    /// resulting work area, checks widget order after save/reload and always
    /// restores the shell before exiting. The watchdog gets a hard timeout too.
    /// </summary>
    private async Task RunSelfTestAsync()
    {
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string outputRoot = Environment.GetEnvironmentVariable("NEBULA_SELFTEST_OUTPUT") ??
                            Path.Combine(Environment.CurrentDirectory, "artifacts", "selftest");
        string outputDir = Path.Combine(outputRoot, stamp);
        Directory.CreateDirectory(outputDir);

        string dataDir = Path.Combine(Path.GetTempPath(), "NebulaSelfTest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);
        Environment.SetEnvironmentVariable("NEBULA_DATA_DIR", dataDir);

        var checks = new List<SelfTestCheck>();
        var report = new StringBuilder();
        var failSafe = new DispatcherTimer(DispatcherPriority.Send)
        {
            Interval = TimeSpan.FromSeconds(75)
        };
        failSafe.Tick += (_, _) =>
        {
            NativeTaskbarController.RestoreNow();
            Log.Write("SELFTEST hard timeout: taskbar restored and process terminated.");
            TryWriteReport(outputDir, checks, "TIMEOUT");
            Environment.Exit(2);
        };
        failSafe.Start();

        SettingsStore? store = null;
        DockWindow? dock = null;
        IslandWindow? island = null;
        SettingsWindow? studio = null;
        NativeTaskbarController? native = null;
        string? fatal = null;
        bool restoredVisible = false;
        int originalWorkBottom = 0;
        int hiddenWorkBottom = 0;

        try
        {
            Write(report, $"Nebula self-test {stamp}");
            Write(report, $"Data: {dataDir}");
            Write(report, $"Output: {outputDir}");
            Write(report, $"OS: {Environment.OSVersion}");

            // 1. Isolated settings and widget round-trip through the real store.
            store = new SettingsStore();
            AppSettings settings = store.Settings;
            settings.ReplaceWindowsTaskbar = true;
            settings.ReserveTaskbarSpace = true;
            settings.TaskbarFullWidth = true;
            settings.DockOnTop = false;
            settings.AnimationsEnabled = false;
            settings.EdgeMargin = 10;
            settings.TaskbarWidgets = new List<string> { "clock", "cpu", "separator", "battery" };
            store.SaveNow();
            string firstOrder = string.Join(",", settings.TaskbarWidgets);
            store.Dispose();
            store = new SettingsStore();
            settings = store.Settings;
            string reloadedOrder = string.Join(",", settings.TaskbarWidgets);
            Add(checks, "settings.widgets.roundtrip",
                firstOrder == "clock,cpu,separator,battery" && reloadedOrder == firstOrder,
                $"written={firstOrder}; reloaded={reloadedOrder}; file={store.FilePath}");

            Forms.Screen screen = ScreenGeometry.Target(settings.MonitorIndex);
            double scale = ScreenGeometry.Scale(screen);
            Write(report, $"Screen: {screen.Bounds.Width}x{screen.Bounds.Height} px; DPI scale={scale:0.##}");
            Write(report, "150% coverage: 1920x1080 at 150% has a 1280x720 DIP work area, so the 1280x720 layout run below is the same logical layout. The host DPI was not changed.");
            originalWorkBottom = WorkArea().Bottom;

            // 2. Refuse to hide unless watchdog + recovery state are usable.
            bool preflight = NativeTaskbarController.Preflight(out string preflightReason);
            Add(checks, "taskbar.preflight", preflight, preflight ? "watchdog, Shell_TrayWnd and recovery state ready" : preflightReason);
            if (!preflight)
                throw new InvalidOperationException("Preflight failed: " + preflightReason);

            var theme = new ThemeEngine(settings);

            // The Liquid preset must resolve to the shared S.* resources, not
            // only be accepted by the settings validator.
            string originalSurface = settings.SurfaceStyle;
            settings.SurfaceStyle = "liquid";
            theme.Reload();
            object radiusValue = Application.Current.Resources["S.CardRadius"];
            object brushValue = Application.Current.Resources["S.CardBg"];
            bool liquidRadius = radiusValue is CornerRadius liquidCorner &&
                                Math.Abs(liquidCorner.TopLeft - 24) < 0.01;
            bool liquidBrush = brushValue is LinearGradientBrush;
            Add(checks, "theme.liquid_surface", liquidRadius && liquidBrush && ThemePreset.All.Count >= 19,
                $"themes={ThemePreset.All.Count}; radius={radiusValue}; brush={brushValue?.GetType().Name}");
            settings.SurfaceStyle = originalSurface;
            theme.Reload();

            var tracker = new WindowTracker();
            var monitor = new SystemMonitor();
            var dockVm = new DockViewModel(settings, theme, tracker, monitor);
            native = new NativeTaskbarController();

            // 3. Real native hiding + real AppBar reservation.
            bool applied = native.Apply(true, 60);
            Add(checks, "taskbar.native.apply", applied && native.IsApplied,
                $"Apply={applied}; IsApplied={native.IsApplied}");
            if (!applied)
                throw new InvalidOperationException("Native taskbar hiding failed; native bar was left visible.");

            hiddenWorkBottom = WorkArea().Bottom;
            bool hidden = !NativeTaskbarController.AnyTrayVisible();
            Add(checks, "taskbar.native.hidden_all_monitors", hidden,
                $"AnyTrayVisible={NativeTaskbarController.AnyTrayVisible()}");

            dock = new DockWindow(dockVm, store);
            dock.ShowActivated = false;
            dock.Show();
            dock.ApplySettings();
            await WaitUntilAsync(() => dock.IsAppBarRegistered && dock.AppBarApplySucceeded, 4000);
            await Task.Delay(650);
            dock.UpdateLayout();

            Add(checks, "appbar.registered", dock.IsAppBarRegistered,
                $"IsRegistered={dock.IsAppBarRegistered}");
            Add(checks, "appbar.setpos", dock.AppBarApplySucceeded,
                $"ApplySucceeded={dock.AppBarApplySucceeded}");

            Rect shellDip = dock.ShellBoundsDip;
            Point shellTopLeft = dock.PointToScreen(new Point(shellDip.Left, shellDip.Top));
            Point shellBottomRight = dock.PointToScreen(new Point(shellDip.Right, shellDip.Bottom));
            double expectedShellWidthPx = screen.Bounds.Width - 2 * settings.EdgeMargin * scale;
            double shellWidthPx = shellBottomRight.X - shellTopLeft.X;
            double expectedShellLeftPx = screen.Bounds.Left + settings.EdgeMargin * scale;
            double expectedShellTopPx = screen.Bounds.Bottom - settings.EdgeMargin * scale - shellDip.Height * scale;
            var dockRoot = (FrameworkElement)dock.FindName("Root");
            var dockShell = (FrameworkElement)dock.FindName("Shell");
            string geometryDetail = $"shellWidth={shellWidthPx:0.#}px expected={expectedShellWidthPx:0.#}px; left={shellTopLeft.X:0.#}px expected={expectedShellLeftPx:0.#}px; " +
                                    $"window.Width={dock.Width:0.#} actual={dock.ActualWidth:0.#} max={dock.MaxWidth:0.#}; root={dockRoot.ActualWidth:0.#}; shell={dockShell.ActualWidth:0.#}; shellDip={shellDip.Width:0.#}";
            Add(checks, "dock.full_width", Math.Abs(shellWidthPx - expectedShellWidthPx) <= 2.5 &&
                Math.Abs(shellTopLeft.X - expectedShellLeftPx) <= 2.5,
                geometryDetail);
            Add(checks, "dock.bottom_edge", Math.Abs(shellTopLeft.Y - expectedShellTopPx) <= 3.0,
                $"shellTop={shellTopLeft.Y:0.#}px expected={expectedShellTopPx:0.#}px; shellHeight={shellDip.Height:0.#}dip");

            int afterBottom = WorkArea().Bottom;
            int reservedPx = hiddenWorkBottom - afterBottom;
            double expectedReservedPx = (shellDip.Height + settings.EdgeMargin) * scale;
            Add(checks, "appbar.work_area_reserved", Math.Abs(reservedPx - expectedReservedPx) <= 7.0 &&
                Math.Abs(afterBottom - shellTopLeft.Y) <= 3.0,
                $"workAreaBottom {hiddenWorkBottom}->{afterBottom}; reserved={reservedPx}px expected={expectedReservedPx:0.#}px; shellTop={shellTopLeft.Y:0.#}px");

            // 4. Widgets in the exact saved order, both in the view model and the visual tree.
            string expectedWidgets = string.Join(",", settings.TaskbarWidgets);
            string actualWidgets = string.Join(",", dockVm.TaskbarWidgetItems.Select(w => w.Id));
            var widgetHost = dock.FindName("TaskbarWidgets") as ItemsControl;
            Add(checks, "widgets.order_initial", actualWidgets == expectedWidgets && widgetHost?.Items.Count == settings.TaskbarWidgets.Count,
                $"expected={expectedWidgets}; viewModel={actualWidgets}; visual={widgetHost?.Items.Count}");

            // 5. A maximized window must stop above the bar.
            // WinForms uses the live work area, unlike WPF's cached
            // SystemParameters.WorkArea, so this is a real maximize test.
            var probe = new Forms.Form
            {
                FormBorderStyle = Forms.FormBorderStyle.Sizable,
                ShowInTaskbar = false,
                StartPosition = Forms.FormStartPosition.Manual,
                Location = new System.Drawing.Point(screen.Bounds.Left, screen.Bounds.Top),
                Size = new System.Drawing.Size(900, 600),
                WindowState = Forms.FormWindowState.Maximized
            };
            probe.Show();
            Forms.Application.DoEvents();
            await Task.Delay(450);
            System.Drawing.Point clientBottom = probe.PointToScreen(new System.Drawing.Point(0, probe.ClientSize.Height));
            int frameBottom = probe.Bounds.Bottom;
            Add(checks, "maximized.window_respects_appbar", clientBottom.Y <= shellTopLeft.Y + 3.0,
                $"maximizedClientBottom={clientBottom.Y:0.#}px; frameBottom={frameBottom}px; shellTop={shellTopLeft.Y:0.#}px; formBounds={probe.Bounds}; client={probe.ClientSize}");
            probe.Close();

            // 5b. The same AppBar contract on the top edge.
            settings.DockOnTop = true;
            dock.Reposition();
            await Task.Delay(450);
            dock.UpdateLayout();
            Rect topShell = dock.ShellBoundsDip;
            Point topOrigin = dock.PointToScreen(new Point(topShell.Left, topShell.Top));
            int topWorkTop = WorkArea().Top;
            int topExpected = (int)Math.Round(topOrigin.Y + topShell.Height * scale);
            Add(checks, "appbar.top_reserved", Math.Abs(topWorkTop - topExpected) <= 7.0 &&
                Math.Abs(topOrigin.Y - screen.Bounds.Top - settings.EdgeMargin * scale) <= 3.0,
                $"workAreaTop={topWorkTop}px; expected={topExpected}px; shellTop={topOrigin.Y:0.#}px");
            settings.DockOnTop = false;
            dock.Reposition();
            await Task.Delay(450);
            dock.UpdateLayout();

            // 6. Explorer restart path: TaskbarCreated re-registers the AppBar and the guard re-hides the shell.
            IntPtr dockHandle = new WindowInteropHelper(dock).Handle;
            uint taskbarCreated = NativeMethods.RegisterWindowMessage("TaskbarCreated");
            NativeMethods.SendMessage(dockHandle, taskbarCreated, IntPtr.Zero, IntPtr.Zero);
            await Task.Delay(650);
            native.EnsureProtection();
            await Task.Delay(300);
            Add(checks, "explorer_restart.appbar_reregistered", dock.IsAppBarRegistered && dock.AppBarApplySucceeded,
                $"TaskbarCreated sent; IsRegistered={dock.IsAppBarRegistered}; ApplySucceeded={dock.AppBarApplySucceeded}");
            Add(checks, "explorer_restart.native_rehidden", !NativeTaskbarController.AnyTrayVisible(),
                $"AnyTrayVisible={NativeTaskbarController.AnyTrayVisible()}");

            Capture(dock.Shell, Path.Combine(outputDir, "taskbar.png"));

            // 7. Widget mutation, persistence and visual refresh.
            settings.TaskbarWidgets = new List<string> { "battery", "separator", "clock", "cpu", "network" };
            store.SaveNow();
            dockVm.RefreshTaskbarWidgets();
            await Task.Delay(350);
            string mutated = string.Join(",", dockVm.TaskbarWidgetItems.Select(w => w.Id));
            int visualMutated = widgetHost?.Items.Count ?? -1;
            using (var verifyStore = new SettingsStore())
            {
                string persisted = string.Join(",", verifyStore.Settings.TaskbarWidgets);
                Add(checks, "widgets.reorder_persisted", mutated == "battery,separator,clock,cpu,network" &&
                    persisted == mutated && visualMutated == 5,
                    $"viewModel={mutated}; reloaded={persisted}; visual={visualMutated}");
            }

            // 8. Dynamic Island: persisted options, applied style resources,
            // compact/expanded geometry and click-through outside the capsule.
            settings.IslandStyle = "pill";
            settings.IslandScale = 1.1;
            settings.IslandTopMargin = 12;
            settings.ShowIsland = true;
            store.SaveNow();
            using (var islandStore = new SettingsStore())
            {
                AppSettings saved = islandStore.Settings;
                Add(checks, "island.settings_roundtrip",
                    saved.ShowIsland && saved.IslandStyle == "pill" &&
                    Math.Abs(saved.IslandScale - 1.1) < 0.001 &&
                    Math.Abs(saved.IslandTopMargin - 12) < 0.001,
                    $"show={saved.ShowIsland}; style={saved.IslandStyle}; scale={saved.IslandScale}; margin={saved.IslandTopMargin}");
            }

            island = new IslandWindow(dockVm) { ShowActivated = false };
            island.Show();
            await WaitUntilAsync(() => island.IsLoaded, 4000);
            await Task.Delay(350);
            island.ApplySettings();
            island.UpdateLayout();

            double compactWidth = island.CapsuleWidth;
            bool islandStyleApplied = island.AppliedStyleKey == "pill" &&
                                      island.CapsuleBackground is not null &&
                                      island.CapsuleCornerRadius > 0 &&
                                      Math.Abs(island.CompactWidth - 242) < 1.0 &&
                                      Math.Abs(island.ExpandedWidth - 440) < 1.0;
            Capture(island.Capsule, Path.Combine(outputDir, "island-compact.png"));

            island.ExpandForTest();
            await Task.Delay(250);
            island.UpdateLayout();
            Rect islandRect = island.CapsuleScreenRect;
            bool islandExpanded = island.IsExpanded &&
                                  island.CapsuleWidth > compactWidth + 10 &&
                                  island.CapsuleHeight > island.CompactHeight + 10;
            Capture(island.Capsule, Path.Combine(outputDir, "island-expanded.png"));

            bool inside = island.IsPointInteractive(new Point(
                islandRect.Left + islandRect.Width / 2,
                islandRect.Top + islandRect.Height / 2));
            bool outside = island.IsPointInteractive(new Point(
                islandRect.Right + 40, islandRect.Bottom + 40));

            Add(checks, "island.style_resources", islandStyleApplied,
                $"style={island.AppliedStyleKey}; background={island.CapsuleBackground?.GetType().Name}; " +
                $"radius={island.CapsuleCornerRadius:0.#}; compact={island.CompactWidth:0.#}x{island.CompactHeight:0.#}; " +
                $"expanded={island.ExpandedWidth:0.#}x{island.ExpandedHeight:0.#}");
            Add(checks, "island.expand", islandExpanded,
                $"compactWidth={compactWidth:0.#}; capsule={island.CapsuleWidth:0.#}x{island.CapsuleHeight:0.#}; " +
                $"expanded={island.IsExpanded}");
            Add(checks, "island.hit_test_transparent_outside", inside && !outside,
                $"inside={inside}; outside={outside}; rect={islandRect}");

            island.Close();
            island = null;

            // 9. Studio layout at common sizes, plus screenshots of every page.
            var tabs = new TabGroupService(settings);
            var icons = new SystemIconService();
            var updates = new UpdateCoordinator(settings);
            var studioVm = new SettingsViewModel(settings, dockVm, theme, monitor, tabs, icons, updates);
            studio = new SettingsWindow(studioVm, store) { ShowActivated = false };
            studio.Show();
            await WaitUntilAsync(() => studio.IsLoaded, 4000);
            await Task.Delay(500);

            await MeasureStudioAsync(studio, 1280, 720, "1280x720", outputDir, checks);
            await MeasureStudioAsync(studio, 1920, 1080, "1920x1080", outputDir, checks);
        }
        catch (Exception ex)
        {
            fatal = ex.ToString();
            Add(checks, "selftest.completed", false, ex.Message);
            Log.Write("SELFTEST exception: " + ex);
        }
        finally
        {
            failSafe.Stop();
            try { island?.Close(); } catch { }
            try { studio?.Close(); } catch { }
            try { dock?.Close(); } catch { }
            try { native?.Restore(); } catch { }
            try { NativeTaskbarController.RestoreNow(); } catch { }
            await Task.Delay(500);
            restoredVisible = NativeTaskbarController.AnyTrayVisible();
            int restoredBottom = WorkArea().Bottom;
            Add(checks, "shutdown.native_visible", restoredVisible,
                $"AnyTrayVisible={restoredVisible}");
            Add(checks, "shutdown.work_area_restored", Math.Abs(restoredBottom - originalWorkBottom) <= 5,
                $"workAreaBottom={restoredBottom}; expected={originalWorkBottom}");
            Add(checks, "shutdown.appbar_released", dock is null || !dock.IsAppBarRegistered,
                $"registered={(dock?.IsAppBarRegistered.ToString() ?? "no dock")}");

            try { store?.Dispose(); } catch { }
            if (Environment.GetEnvironmentVariable("NEBULA_KEEP_TEST_DATA") == "1")
                Write(report, "Data kept for restart verification: " + dataDir);
            else
                try { Directory.Delete(dataDir, true); } catch { }

            bool passed = checks.Count > 0 && checks.All(c => c.Passed) && fatal is null;
            Write(report, "");
            foreach (SelfTestCheck check in checks)
                Write(report, $"[{(check.Passed ? "PASS" : "FAIL")}] {check.Name}: {check.Detail}");
            Write(report, $"RESULT: {(passed ? "PASS" : "FAIL")}");
            if (fatal is not null)
                Write(report, fatal);

            TryWriteReport(outputDir, checks, passed ? "PASS" : "FAIL", report.ToString());
            Log.Write($"SELFTEST {(passed ? "PASS" : "FAIL")}; report={Path.Combine(outputDir, "selftest-report.txt")}");
            Shutdown(passed ? 0 : 1);
        }
    }

    private async Task MeasureStudioAsync(SettingsWindow studio, double width, double height,
        string label, string outputDir, List<SelfTestCheck> checks)
    {
        studio.Width = width;
        studio.Height = height;
        studio.UpdateLayout();
        await Task.Delay(180);

        MethodInfo showPage = typeof(SettingsWindow).GetMethod("ShowPage",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        MethodInfo normalize = typeof(SettingsWindow).GetMethod("NormalizeSettingRows",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var pages = new (string Key, string Element)[]
        {
            ("style", "PageStyle"), ("dock", "PageDock"), ("icons", "PageSystemIcons"),
            ("tabs", "PageTabs"), ("widgets", "PageWidget"), ("search", "PageSearch"),
            ("controls", "PageControls"), ("motion", "PageAnimations"),
            ("behavior", "PageBehavior"), ("info", "PageInfo")
        };

        foreach ((string key, string elementName) in pages)
        {
            RadioButton? nav = FindVisualChildren<RadioButton>(studio).FirstOrDefault(r => r.Uid == key);
            if (nav is not null)
                nav.IsChecked = true;
            else
                showPage.Invoke(studio, new object[] { key });
            await Task.Delay(220);
            normalize.Invoke(studio, null);
            studio.UpdateLayout();

            var scroll = (ScrollViewer)studio.FindName("ContentScroll");
            var page = (FrameworkElement)studio.FindName(elementName);
            var rowStyle = studio.TryFindResource("Row") as Style;
            int overlaps = 0;
            int clipped = 0;
            var samples = new List<string>();
            foreach (Border row in FindVisualChildren<Border>(page))
            {
                if (!row.IsVisible || !ReferenceEquals(row.Style, rowStyle) || row.Child is not Grid grid)
                    continue;
                if (grid.ColumnDefinitions.Count < 2 || grid.Children.Count < 2)
                    continue;
                if (grid.Children[0] is not FrameworkElement a || grid.Children[1] is not FrameworkElement b)
                    continue;
                Rect ar = a.TransformToAncestor(scroll).TransformBounds(new Rect(a.RenderSize));
                Rect br = b.TransformToAncestor(scroll).TransformBounds(new Rect(b.RenderSize));
                bool realOverlap = ar.Right > br.Left + 0.5 && br.Right > ar.Left + 0.5 &&
                                   ar.Bottom > br.Top + 0.5 && br.Bottom > ar.Top + 0.5;
                if (realOverlap)
                {
                    overlaps++;
                    if (samples.Count < 4)
                        samples.Add($"overlap {a.GetType().Name}/{b.GetType().Name} a={ar} b={br} grid={grid.ActualWidth:0.#} cols={grid.ColumnDefinitions[0].ActualWidth:0.#}/{grid.ColumnDefinitions[1].ActualWidth:0.#}");
                }
                if (br.Right > scroll.ViewportWidth + 1.5 || br.Left < -1.5)
                {
                    clipped++;
                    if (samples.Count < 4)
                        samples.Add($"clip {b.GetType().Name} b={br} viewport={scroll.ViewportWidth:0.#} grid={grid.ActualWidth:0.#}");
                }
            }

            bool horizontalClip = scroll.ExtentWidth > scroll.ViewportWidth + 1.5;
            scroll.ScrollToBottom();
            studio.UpdateLayout();
            await Task.Delay(60);
            bool canReachBottom = scroll.ScrollableHeight <= 0.5 ||
                                  scroll.VerticalOffset + scroll.ViewportHeight >= scroll.ExtentHeight - 2;
            scroll.ScrollToTop();
            studio.UpdateLayout();
            await Task.Delay(40);

            bool pass = !horizontalClip && overlaps == 0 && clipped == 0 && canReachBottom;
            Add(checks, $"layout.{label}.{key}", pass,
                $"viewport={scroll.ViewportWidth:0.#}x{scroll.ViewportHeight:0.#}dip; extent={scroll.ExtentWidth:0.#}x{scroll.ExtentHeight:0.#}dip; hclip={horizontalClip}; overlaps={overlaps}; clippedControls={clipped}; bottomReachable={canReachBottom}" +
                (samples.Count > 0 ? "; samples=" + string.Join(" | ", samples) : ""));

            Capture((FrameworkElement)studio.FindName("Chrome"),
                Path.Combine(outputDir, $"studio-{label}-{key}.png"));
        }
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                yield return match;
            foreach (T descendant in FindVisualChildren<T>(child))
                yield return descendant;
        }
    }

    private static void Capture(FrameworkElement element, string path)
    {
        try
        {
            element.UpdateLayout();
            int width = (int)Math.Ceiling(element.ActualWidth);
            int height = (int)Math.Ceiling(element.ActualHeight);
            if (width < 2 || height < 2)
                return;
            // RenderTargetBitmap.Render(visual) includes the visual's offset
            // inside its parent. A VisualBrush draws the element from its own
            // top-left corner, so centred elements (the Dynamic Island) are not
            // clipped in their screenshot.
            var visual = new DrawingVisual();
            using (DrawingContext context = visual.RenderOpen())
            {
                var brush = new VisualBrush(element)
                {
                    Stretch = Stretch.None,
                    AlignmentX = AlignmentX.Left,
                    AlignmentY = AlignmentY.Top
                };
                context.DrawRectangle(brush, null, new Rect(0, 0, width, height));
            }

            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using FileStream stream = File.Create(path);
            encoder.Save(stream);
        }
        catch (Exception ex)
        {
            Log.Write("SELFTEST screenshot failed: " + ex.Message);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs)
    {
        int waited = 0;
        while (!condition() && waited < timeoutMs)
        {
            await Task.Delay(50);
            waited += 50;
        }
    }

    private static void Add(List<SelfTestCheck> checks, string name, bool passed, string detail)
    {
        checks.Add(new SelfTestCheck(name, passed, detail));
        Log.Write($"SELFTEST {name}: {(passed ? "PASS" : "FAIL")} - {detail}");
    }

    private static void Write(StringBuilder report, string line) => report.AppendLine(line);

    private static void TryWriteReport(string outputDir, List<SelfTestCheck> checks, string result, string? existing = null)
    {
        try
        {
            Directory.CreateDirectory(outputDir);
            var text = new StringBuilder(existing ?? "");
            if (existing is null)
            {
                text.AppendLine("RESULT: " + result);
                foreach (SelfTestCheck check in checks)
                    text.AppendLine($"[{(check.Passed ? "PASS" : "FAIL")}] {check.Name}: {check.Detail}");
            }
            File.WriteAllText(Path.Combine(outputDir, "selftest-report.txt"), text.ToString());
        }
        catch
        {
            // The app log still contains the individual checks.
        }
    }

    private static NativeRect WorkArea()
    {
        if (!SystemParametersInfo(0x0030, 0, out NativeRect rect, 0))
            return default;
        return rect;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, out NativeRect rect, uint winIni);
}