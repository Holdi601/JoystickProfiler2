using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using System.Reflection;
using Avalonia.Controls.Presenters;
using Avalonia.VisualTree;
using JoyPro.Gui.Models;
using JoyPro.Gui.ViewModels;
using JoyPro.Gui.Views;
using JoyPro.Gui.Views.Controls;
using Xunit;

namespace JoyPro.Gui.Tests;

// The Settings page in the real main window, clicked like a mouse does: a press and a release go into the window's
// input (hit test, pointer capture, Click, Tapped), exactly on the chevron of each section header and on the empty part
// of its header row. Every Expander of the app the same.
[SupportedOSPlatform("windows")]
public sealed class HeaderPointerTests
{
    // Avalonia's raw input (what the Win32 window hands over for a real mouse) through reflection: the types are not
    // public API, the path behind them is the real one (hit test, capture, Click, Tapped).
    internal static object NewMouse() =>
        Activator.CreateInstance(typeof(IPointer).Assembly.GetType("Avalonia.Input.MouseDevice")!, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [null], null)!;

    internal static void Click(TopLevel top, object mouse, Point at)
    {
        object impl = typeof(TopLevel).GetProperty("PlatformImpl", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(top)!;
        PropertyInfo inputProperty = impl.GetType().GetInterfaces().Concat([impl.GetType()]).Select(t => t.GetProperty("Input", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            .First(p => p != null)!;
        Delegate input = (Delegate)inputProperty.GetValue(impl)!;
        Type raw = typeof(IPointer).Assembly.GetType("Avalonia.Input.Raw.RawPointerEventArgs")!;
        Type kind = typeof(IPointer).Assembly.GetType("Avalonia.Input.Raw.RawPointerEventType")!;
        Type modifiers = typeof(IPointer).Assembly.GetType("Avalonia.Input.RawInputModifiers")!;
        ConstructorInfo create = raw.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .First(c => c.GetParameters().Length == 6 && c.GetParameters()[4].ParameterType == typeof(Point));
        // The composed scene follows a scroll a frame later: wait until the point hits the page, not the window's back.
        for (int i = 0; i < 50 && top.InputHitTest(at) is Panel { Children.Count: 0 }; i++)
        {
            AvaloniaRenderThread.Pump();
            System.Threading.Thread.Sleep(20);
        }
        ulong time = (ulong)Environment.TickCount64;
        void Send(string type, string modifier, ulong t) =>
            input.DynamicInvoke(create.Invoke([mouse, t, top, Enum.Parse(kind, type), at, Enum.Parse(modifiers, modifier)]));
        Send("Move", "None", time);
        Send("LeftButtonDown", "LeftMouseButton", time + 1);
        AvaloniaRenderThread.Pump();
        Send("LeftButtonUp", "None", time + 2);
        AvaloniaRenderThread.Pump();
    }

    // These tests need a real, shown window (real pointer input and the composed scene). A test window must never
    // appear on the user's desktop (it did, although placed far off the screen), so they run only with
    // JOYPRO_TEST_WINDOWS=1; everything else renders the page in a hidden root (HiddenWindow).
    internal static bool WindowsAllowed => Environment.GetEnvironmentVariable("JOYPRO_TEST_WINDOWS") == "1";

    // A real window far off the screen, never activated (no focus taken from other programs), so that the compositor
    // has a scene and hit tests the way the user's window does. Only with WindowsAllowed.
    internal static T OffscreenWindow<T>(T window, Size size) where T : Window
    {
        if (!WindowsAllowed)
            throw new InvalidOperationException("A shown test window needs JOYPRO_TEST_WINDOWS=1 (use HiddenWindow).");
        window.Width = size.Width;
        window.Height = size.Height;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.SystemDecorations = SystemDecorations.None;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Position = new PixelPoint(-30000, -30000);
        window.Show();
        Lay(window, size);
        Settle(window);
        return window;
    }

    // Layout and a composition pass (hit tests read the composed scene).
    internal static void Settle(Window window)
    {
        for (int i = 0; i < 10; i++)
        {
            AvaloniaRenderThread.Pump();
            System.Threading.Thread.Sleep(20);
        }
    }

    internal static void Lay(Window window, Size size)
    {
        for (int i = 0; i < 4; i++)
        {
            window.Measure(size);
            window.Arrange(new Rect(size));
            AvaloniaRenderThread.Pump();
        }
    }

    private static string Describe(IInputElement? hit) => (hit is Panel p0 ? "[bounds " + p0.Bounds + " bg " + p0.Background + " children " + string.Join(",", p0.Children.Select(ch => ch.GetType().Name + "#" + ch.Name + " vis=" + ch.IsVisible + " " + ch.Bounds)) + "] " : string.Empty) + (hit is Visual v ? string.Join(" < ", v.GetSelfAndVisualAncestors().Take(8).Select(a => a.GetType().Name + (a is Control { Name: { Length: > 0 } n } ? "#" + n : string.Empty))) : "nothing");

    // Scrolls the Settings page so that the control's top is a little below the top of the visible part.
    internal static void ScrollTo(MainWindow main, Control target)
    {
        ScrollViewer scroller = main.FindControl<ScrollViewer>("SettingsScrollViewer")!;
        Settle(main);
        Control host = (Control)scroller.Content!;
        double y = target.TranslatePoint(new Point(0, 0), host)!.Value.Y;
        scroller.Offset = new Vector(0, Math.Max(0, y - 20));
        Settle(main);
    }

    // Opt-in picture of the window as it is (set JOYPRO_GUI_PARITY_PREVIEW to a folder).
    internal static void Snap(Window window, Size size, string name)
    {
        string? folder = Environment.GetEnvironmentVariable("JOYPRO_GUI_PARITY_PREVIEW");
        if (string.IsNullOrWhiteSpace(folder))
            return;
        System.IO.Directory.CreateDirectory(folder);
        using Avalonia.Media.Imaging.RenderTargetBitmap bitmap = new(new PixelSize((int)size.Width, (int)size.Height));
        bitmap.Render(window);
        bitmap.Save(System.IO.Path.Combine(folder, name));
    }

    internal static string Under(Visual root, Point at)
    {
        List<string> lines = [];
        foreach (Visual v in root.GetVisualDescendants())
        {
            Point? o = v.TranslatePoint(new Point(0, 0), root);
            if (o == null || !new Rect(o.Value, v.Bounds.Size).Contains(at) || !v.IsEffectivelyVisible)
                continue;
            int depth = v.GetVisualAncestors().Count();
            lines.Add(depth + " " + v.GetType().Name + (v is Control { Name: { Length: > 0 } n } ? "#" + n : string.Empty) + (v is InputElement ie && !ie.IsHitTestVisible ? " [nohit]" : string.Empty) + (v.ClipToBounds ? " clip" : string.Empty) + " " + new Rect(o.Value, v.Bounds.Size));
        }
        return string.Join("\n", lines.TakeLast(25));
    }

    private static Point Center(Visual target, Visual root)
    {
        Rect bounds = target.Bounds;
        Point? at = target.TranslatePoint(new Point(bounds.Width / 2, bounds.Height / 2), root);
        Assert.NotNull(at);
        return at!.Value;
    }

    // The tabs at the top of the Settings page: a click opens or closes its section, a double click shows only it; the
    // drag handle clicked without moving toggles too. Real pointer input in the real window.
    [Fact]
    public void SettingsTabs_ClickTogglesDoubleClickShowsOnlyThatSection_AndTheHandleClickToggles()
    {
        if (!WindowsAllowed)
            return;
        AvaloniaRenderThread.Run(() =>
        {
            MainWindowViewModel vm = new(new DescribeGroupFakeEngine(), new AxisTuningFakeCapture(), loadPersistedUiSettings: false)
            {
                EnginePath = typeof(MainWindowViewModel).Assembly.Location
            };
            vm.CurrentStage = UiStage.Settings;
            vm.CollapseAllSettingsCommand.Execute(null);
            vm.FindSettingsSection("games")!.IsExpanded = true;
            Size size = new(1280, 800);
            MainWindow main = OffscreenWindow(new MainWindow { DataContext = vm }, size);
            object mouse = NewMouse();
            SettingsTabStrip strip = main.GetVisualDescendants().OfType<SettingsTabStrip>().Single();
            Border Tab(string id) => strip.Children.OfType<Border>().Single(b => b.Tag is SettingsSectionItem { Id: var tabId } && tabId == id);
            SettingsSectionItem localAi = vm.FindSettingsSection(MainWindowViewModel.LocalAiSectionId)!;
            Assert.Contains("Double-click", ToolTip.GetTip(Tab(MainWindowViewModel.LocalAiSectionId))?.ToString());

            // Click: open (and it scrolls there), click again: closed.
            Click(main, mouse, Center(Tab(MainWindowViewModel.LocalAiSectionId), main));
            Assert.True(localAi.IsExpanded);
            Assert.True(vm.FindSettingsSection("games")!.IsExpanded); // a click leaves the others as they are
            Settle(main);
            System.Threading.Thread.Sleep(600); // a second click, not a double click
            Click(main, mouse, Center(Tab(MainWindowViewModel.LocalAiSectionId), main));
            Assert.False(localAi.IsExpanded);

            // Double click: only this section open.
            Settle(main);
            System.Threading.Thread.Sleep(600); // not a double click with the click before
            Point at = Center(Tab("components"), main);
            Click(main, mouse, at);
            Click(main, mouse, at);
            Assert.True(vm.FindSettingsSection("components")!.IsExpanded);
            Assert.Equal(["components"], vm.SettingsSections.Where(s => s.IsExpanded).Select(s => s.Id).ToArray());
            Snap(main, size, "settings-tabs-only-components.png");

            // The drag handle, clicked without moving, opens and closes like the rest of the row.
            Settle(main);
            SettingsSection components = main.GetVisualDescendants().OfType<SettingsSection>().Single(s => s.SectionId == "components");
            Control grip = components.GetVisualDescendants().OfType<Control>().First(c => c.Name == "PART_Grip");
            System.Threading.Thread.Sleep(600);
            Click(main, mouse, Center(grip, main));
            Assert.False(vm.FindSettingsSection("components")!.IsExpanded);
            // Enter on the focused header toggles from the keyboard.
            Control toggle = components.GetVisualDescendants().OfType<Control>().First(c => c.Name == "PART_Toggle");
            toggle.Focus();
            toggle.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Enter });
            Assert.True(vm.FindSettingsSection("components")!.IsExpanded);
            main.Close();
        });
    }

    // Opt-in pictures (JOYPRO_GUI_PARITY_PREVIEW): Settings > Local AI and > Components with the user's state (Qwen3 8B
    // downloaded and on, not loaded), the Manage with AI panel with its status line, in the real window at both sizes.
    [Fact]
    public void RenderLocalAiSettingsComponentsAndManagePanel()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("JOYPRO_GUI_PARITY_PREVIEW")) || !WindowsAllowed)
            return;
        AvaloniaRenderThread.Run(() =>
        {
            foreach ((int w, int h) in new[] { (1280, 800), (2267, 1250) })
            {
                LocalAiFakeEngine engine = new();
                MainWindowViewModel vm = new(engine, new AxisTuningFakeCapture(), loadPersistedUiSettings: false)
                {
                    EnginePath = typeof(MainWindowViewModel).Assembly.Location,
                    ProfilePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "JoyPro2.Gui.Tests", "local-ai-render.pr0file")
                };
                AvaloniaRenderThread.Wait(vm.RefreshRelationsCommand.ExecuteAsync(null));
                Size size = new(w, h);
                vm.CurrentStage = UiStage.Relations;
                MainWindow main = OffscreenWindow(new MainWindow { DataContext = vm }, size);
                vm.ShowManageWithAiCommand.Execute(null);
                AvaloniaRenderThread.Wait(vm.EnsureLlmStatusAsync());
                vm.AiPlanText = LocalAiTests.UserRequest;
                Settle(main);
                Snap(main, size, "manage-with-ai-chip-" + w + ".png");
                Assert.Contains(main.GetVisualDescendants().OfType<TextBlock>(), t => t.Name == "AiStatusLine" && t.IsEffectivelyVisible &&
                                                                                 t.Text == "AI: on (Qwen3 8B) - not loaded right now, starts with your next request");

                vm.CollapseAllSettingsCommand.Execute(null);
                AvaloniaRenderThread.Wait(vm.OpenLocalAiSettingsCommand.ExecuteAsync(null));
                Settle(main);
                Snap(main, size, "settings-local-ai-" + w + ".png");
                vm.CollapseAllSettingsCommand.Execute(null);
                vm.OpenSettingsSectionCommand.Execute("components");
                Settle(main);
                Control card = main.GetVisualDescendants().OfType<Control>().Single(c => c.Name == "LocalAiComponentCard");
                card.BringIntoView();
                Settle(main);
                Snap(main, size, "settings-components-" + w + ".png");
                Assert.Contains(card.GetVisualDescendants().OfType<PathIcon>(), p => p.IsEffectivelyVisible && p.Classes.Contains("ok"));
                main.Close();
            }
        });
    }

    [Fact]
    public void SettingsSections_PressAndReleaseOnTheChevronAndTheEmptyHeaderRowToggle()
    {
        if (!WindowsAllowed)
            return;
        AvaloniaRenderThread.Run(() =>
        {
            MainWindowViewModel vm = new(new DescribeGroupFakeEngine(), new AxisTuningFakeCapture(), loadPersistedUiSettings: false)
            {
                EnginePath = typeof(MainWindowViewModel).Assembly.Location
            };
            vm.CurrentStage = UiStage.Settings;
            vm.CollapseAllSettingsCommand.Execute(null);
            Size size = new(1280, 800);
            MainWindow main = OffscreenWindow(new MainWindow { DataContext = vm }, size);
            object mouse = NewMouse();
            Snap(main, size, "settings-headers.png");
            List<SettingsSection> sections = main.GetVisualDescendants().OfType<SettingsSection>().Where(s => s.IsEffectivelyVisible).ToList();
            Assert.True(sections.Count >= 10, "sections on the page: " + sections.Count);
            List<string> failures = [];
            // The harness itself: a click on a title (which works in the real window) toggles.
            {
                SettingsSection first = sections[0];
                TextBlock title = first.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == first.Title);
                Point at = Center(title, main);
                IInputElement? hit = main.InputHitTest(at);
                bool was = first.Item!.IsExpanded;
                Click(main, mouse, at);
                Assert.True(first.Item!.IsExpanded != was, "title click did not toggle; hit: " + hit + " " + (hit as Control)?.Name + " at " + at);
                first.Item!.IsExpanded = false;
            }
            foreach (SettingsSection section in sections)
            {
                SettingsSectionItem item = section.Item!;
                Control headerRow = section.GetVisualDescendants().OfType<Control>().First(c => c.Name == "PART_Header");
                ScrollTo(main, headerRow);
                Settle(main);
                PathIcon chevron = section.GetVisualDescendants().OfType<PathIcon>().First(p => p.Name == (item.IsExpanded ? "PART_Opened" : "PART_Closed"));
                bool before = item.IsExpanded;
                Click(main, mouse, Center(chevron, main));
                if (item.IsExpanded == before)
                    failures.Add(item.Id + ": chevron (closed) hit " + Describe(main.InputHitTest(Center(chevron, main))) + " at " + Center(chevron, main) + "\n" + Under(main, Center(chevron, main)));
                Settle(main);
                PathIcon open = section.GetVisualDescendants().OfType<PathIcon>().First(p => p.Name == (item.IsExpanded ? "PART_Opened" : "PART_Closed"));
                before = item.IsExpanded;
                Click(main, mouse, Center(open, main));
                if (item.IsExpanded == before)
                    failures.Add(item.Id + ": chevron (open)");
                // The empty part of the header row, right of the title, left of the section's own buttons.
                Settle(main);
                Control header = section.GetVisualDescendants().OfType<Control>().First(c => c.Name == "PART_Header");
                Control? own = section.GetVisualDescendants().OfType<ContentPresenter>().FirstOrDefault(c => c.Name == "PART_HeaderContent" && c.IsVisible && c.Bounds.Width > 0);
                double right = own != null ? own.TranslatePoint(new Point(0, 0), header)!.Value.X - 6 : header.Bounds.Width - 48;
                Point empty = header.TranslatePoint(new Point(right, header.Bounds.Height / 2), main)!.Value;
                before = item.IsExpanded;
                Click(main, mouse, empty);
                if (item.IsExpanded == before)
                    failures.Add(item.Id + ": empty header row");
                item.IsExpanded = false;
            }
            Assert.True(failures.Count == 0, string.Join("; ", failures));

            // Every Expander inside the sections: its chevron and the empty part of its header.
            vm.ExpandAllSettingsCommand.Execute(null);
            Settle(main);
            List<Expander> expanders = main.GetVisualDescendants().OfType<Expander>().Where(x => x.IsEffectivelyVisible && x.Bounds.Height > 0).ToList();
            Assert.NotEmpty(expanders);
            foreach (Expander expander in expanders)
            {
                string name = expander.Header?.ToString() ?? "?";
                ScrollTo(main, expander);
                Settle(main);
                ToggleButton header = expander.GetVisualDescendants().OfType<ToggleButton>().First(t => t.Name == "ExpanderHeader");
                Visual chevron = header.GetVisualDescendants().OfType<Border>().First(b => b.Name == "ExpandCollapseChevronBorder");
                bool before = expander.IsExpanded;
                Click(main, mouse, Center(chevron, main));
                if (expander.IsExpanded == before)
                    failures.Add("expander " + name + ": chevron");
                Settle(main);
                before = expander.IsExpanded;
                Point empty = header.TranslatePoint(new Point(header.Bounds.Width * 0.7, header.Bounds.Height / 2), main)!.Value;
                Click(main, mouse, empty);
                if (expander.IsExpanded == before)
                    failures.Add("expander " + name + ": empty header");
            }
            Assert.True(failures.Count == 0, string.Join("; ", failures));
            main.Close();
        });
    }
}
