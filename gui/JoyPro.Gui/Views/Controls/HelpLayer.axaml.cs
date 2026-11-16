using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services.Help;
using JoyPro.Gui.ViewModels;

namespace JoyPro.Gui.Views.Controls;

// The layer over the window's content for F1's help mode and the tours (docs/ui/help-and-tours.md). It lies inside the
// UI scale, so its cards scale with the window, and finds controls by name, command or tab, never by position: the
// user's page order, hidden pages, window size, UI scale and theme do not matter. Help mode: every click lands on the
// catcher (help cursor); the control under it is found in the content below and its help shows next to it. Tour: the
// step's control stays clickable through a hole in the dimmed rest; a click on it (or the page change it asks for) goes
// on to the next step.
public partial class HelpLayer : UserControl
{
    private Control? content;
    private MainWindowViewModel? vm;
    private Control? helpTarget;
    private Control? hoverTarget;
    private Control? tourTarget;
    private string tourTargetKey = string.Empty;
    private bool placePending;

    public HelpLayer()
    {
        InitializeComponent();
        HelpCatcher.PointerPressed += CatcherPressed;
        HelpCatcher.PointerMoved += CatcherMoved;
        HelpCatcher.PointerExited += (_, _) => SetHover(null);
        HelpCatcher.PointerWheelChanged += CatcherWheel;
        SizeChanged += (_, _) => RequestPlace();
        HelpCard.SizeChanged += (_, _) => RequestPlace();
        TourCard.SizeChanged += (_, _) => RequestPlace();
        placeTimer.Tick += (_, _) =>
        {
            placeTimer.Stop();
            lastLayoutPlace = Environment.TickCount64;
            RequestPlace();
        };
    }


    // The control the open help popup is about, and the tour step's control (tests).
    public Control? HelpTarget => helpTarget;

    public Control? TourTarget => tourTarget;

    // The window lets go of its view model: the layer stops following it.
    public void Detach()
    {
        if (vm != null)
        {
            vm.PropertyChanged -= ModelChanged;
            vm.TourStepChanged -= OnTourStepChanged;
        }
        if (content != null)
            content.LayoutUpdated -= ContentLayoutUpdated;
        placeTimer.Stop();
        DetachTourClick();
        vm = null;
    }

    public void Attach(Control windowContent, MainWindowViewModel model)
    {
        if (vm != null)
        {
            vm.PropertyChanged -= ModelChanged;
            vm.TourStepChanged -= OnTourStepChanged;
        }
        content = windowContent;
        vm = model;
        vm.PropertyChanged += ModelChanged;
        vm.TourStepChanged += OnTourStepChanged;
        content.LayoutUpdated -= ContentLayoutUpdated;
        content.LayoutUpdated += ContentLayoutUpdated;
        OnTourStepChanged();
    }

    private void ModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainWindowViewModel.IsHelpModeOn):
                if (vm?.IsHelpModeOn != true)
                    SetHover(null);
                break;
            case nameof(MainWindowViewModel.HelpTopic):
                if (vm?.HelpTopic == null)
                    helpTarget = null;
                RequestPlace();
                break;
            case nameof(MainWindowViewModel.IsTourActive):
            case nameof(MainWindowViewModel.IsWelcomeOpen):
            case nameof(MainWindowViewModel.CurrentStage):
                RequestPlace();
                break;
        }
    }

    // Any layout pass of the window (LayoutUpdated is raised for every pass): placed again only when a control moved,
    // so placing (which lays out the layer) does not start the next pass; nothing runs while nothing is shown.
    private void ContentLayoutUpdated(object? sender, EventArgs e)
    {
        if (placePending || vm == null || (!vm.IsTourActive && vm.HelpTopic == null && !vm.IsHelpModeOn))
            return;
        if (Signature() == placedSignature)
            return;
        // At most every 100 ms from layout passes (a page that keeps laying itself out never keeps this busy).
        long now = Environment.TickCount64;
        if (now - lastLayoutPlace >= 100)
        {
            lastLayoutPlace = now;
            RequestPlace();
        }
        else if (!placeTimer.IsEnabled)
        {
            placeTimer.Start();
        }
    }

    private long lastLayoutPlace;

    private readonly DispatcherTimer placeTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };

    private string placedSignature = string.Empty;

    private string Signature() =>
        string.Join("|", Bounds.Size, helpTarget == null ? null : RectOf(helpTarget), tourTarget == null ? null : RectOf(tourTarget),
            hoverTarget == null ? null : RectOf(hoverTarget), tourTarget?.IsEffectivelyVisible);

    // ---- help mode ----

    // The control under a point of this layer (null: an empty place of the page), and the page it is on.
    public Control? TargetAt(Point point, out string page)
    {
        page = vm?.CurrentHelpPage ?? "start";
        if (content == null)
            return null;
        Point? inContent = this.TranslatePoint(point, content);
        if (inContent == null)
            return null;
        Visual? hit = content.GetVisualsAt(inContent.Value, v => v.IsVisible && (v is not InputElement ie || ie.IsHitTestVisible)).FirstOrDefault();
        Control? target = HelpResolver.FindTarget(hit, content);
        page = PageOf(target ?? hit as Control, page);
        return target;
    }

    // The wiki page a control belongs to: the bars at the top and bottom are the profile bar's, Output is its own.
    public static string PageOf(Control? control, string openPage)
    {
        for (Visual? v = control; v != null; v = v.GetVisualParent())
        {
            switch ((v as Control)?.Name)
            {
                case "EngineOutputHostGrid":
                case "EngineOutputExpander":
                    return "output";
                case "BottomBar":
                case "ProfileHeaderGrid":
                case "BrandBanner":
                    return "header";
                case "PagesHost":
                    return openPage;
            }
        }
        return openPage;
    }

    // Help mode's answer for a control (or the page for an empty place); also for tests.
    public HelpTopic ShowHelpFor(Control? target, string page)
    {
        HelpTopic topic = target == null || !HelpResolver.IsHelpTarget(target) ? HelpResolver.PageTopic(page) : HelpResolver.Resolve(target, page);
        helpTarget = target ?? (content?.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == "PagesHost"));
        vm?.ShowHelp(topic);
        RequestPlace();
        return topic;
    }

    private void CatcherPressed(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
        Control? target = TargetAt(e.GetPosition(this), out string page);
        ShowHelpFor(target, page);
    }

    private void CatcherMoved(object? sender, PointerEventArgs e) => SetHover(TargetAt(e.GetPosition(this), out _));

    private void CatcherWheel(object? sender, PointerWheelEventArgs e)
    {
        // The page scrolls in help mode too (the catcher would swallow the wheel otherwise).
        if (content == null || this.TranslatePoint(e.GetPosition(this), content) is not Point inContent)
            return;
        Visual? hit = content.GetVisualsAt(inContent, v => v.IsVisible).FirstOrDefault();
        ScrollViewer? viewer = hit?.FindAncestorOfType<ScrollViewer>(includeSelf: true);
        while (viewer != null && viewer.Extent.Height <= viewer.Viewport.Height + 1)
            viewer = viewer.FindAncestorOfType<ScrollViewer>();
        if (viewer == null)
            return;
        viewer.Offset = new Vector(viewer.Offset.X, Math.Clamp(viewer.Offset.Y - e.Delta.Y * 60, 0, Math.Max(0, viewer.Extent.Height - viewer.Viewport.Height)));
        e.Handled = true;
        RequestPlace();
    }

    private void SetHover(Control? target)
    {
        if (ReferenceEquals(target, hoverTarget))
            return;
        hoverTarget = target;
        RequestPlace();
    }

    // ---- tours ----

    // The step the card shows now (a newer step stops the looks of an older one), and whether its control was scrolled
    // into view again after it was found outside the view.
    private int tourStepVersion;
    private bool tourScrolledAgain;

    private void OnTourStepChanged()
    {
        DetachTourClick();
        tourTarget = null;
        tourTargetKey = vm?.TourTargetKey ?? string.Empty;
        tourScrolledAgain = false;
        tourStepStarted = Environment.TickCount64;
        int version = ++tourStepVersion;
        // The step's page opens first; the control is looked up once the page has its layout (a few tries: a page,
        // tab or section that just opened lays itself out over more than one pass).
        Dispatcher.UIThread.Post(() => LookForTourTarget(version, 0), DispatcherPriority.Loaded);
        RequestPlace();
    }

    private void LookForTourTarget(int version, int attempt)
    {
        if (version != tourStepVersion || vm is not { IsTourActive: true } model || model.IsWelcomeOpen)
            return;
        tourLookedVersion = version;
        FindTourTarget();
        if (tourTarget != null)
        {
            tourTarget.BringIntoView();
            RequestPlace();
            return;
        }
        RequestPlace();
        // Looked again after real layout passes (a page that just opened may still lay itself out), then left out.
        if (!ReportMissingTourTarget(version) && attempt < 6 && PointsAtSomething(tourTargetKey))
            DispatcherTimer.RunOnce(() => LookForTourTarget(version, attempt + 1), TimeSpan.FromMilliseconds(100));
    }

    private long tourStepStarted;
    private int tourLookedVersion = -1;
    private int tourMissingVersion = -1;

    // Nothing to point at in this state, also after about 0.4 s of layout: the step is left out (a step never points at
    // nothing). Only once the window shows its pages (a minimized or not yet shown window finds nothing at all).
    private bool ReportMissingTourTarget(int version)
    {
        if (version != tourStepVersion || tourLookedVersion != version || tourMissingVersion == version || tourTarget != null ||
            vm is not { IsTourActive: true, IsWelcomeOpen: false } model || !PointsAtSomething(tourTargetKey) ||
            Environment.TickCount64 - tourStepStarted < 400 || FindTarget("page") == null)
            return false;
        tourMissingVersion = version;
        Dispatcher.UIThread.Post(() =>
        {
            if (version == tourStepVersion && tourTarget == null)
                model.TourTargetMissing();
        }, DispatcherPriority.Background);
        return true;
    }

    private static bool PointsAtSomething(string key) => !string.IsNullOrWhiteSpace(key) && key != "none";

    public Control? FindTourTarget()
    {
        DetachTourClick();
        OpenStepTab();
        tourTarget = null;
        if (vm?.IsTourActive == true)
        {
            tourTarget = FindTarget(tourTargetKey);
            int alternative = -1;
            IReadOnlyList<TourAlternative> others = vm.TourAlternatives;
            for (int i = 0; tourTarget == null && i < others.Count; i++)
            {
                tourTarget = FindTarget(others[i].Target);
                if (tourTarget != null)
                    alternative = i;
            }
            vm.SetTourAlternative(alternative);
            // A part that opens (the axis curve): opened, so what the card talks about shows.
            if (tourTarget is Expander { IsExpanded: false, IsEnabled: true } expander)
                expander.IsExpanded = true;
        }
        if (tourTarget is Button button && vm?.TourListensForClick == true)
            button.AddHandler(Button.ClickEvent, TourTargetClicked, RoutingStrategies.Bubble, handledEventsToo: true);
        return tourTarget;
    }

    // The step's tab (TourStep.OpenTab, e.g. Live test) is opened once per step, so what the card talks about shows.
    private TourStep? tabOpenedFor;

    private void OpenStepTab()
    {
        TourStep? step = vm?.IsTourActive == true ? vm.CurrentTourStep : null;
        if (step == null || step.OpenTab.Length == 0 || ReferenceEquals(tabOpenedFor, step) || content == null)
            return;
        TabItem? tab = content.GetVisualDescendants().OfType<TabItem>()
            .FirstOrDefault(t => t.IsEffectivelyVisible && t.Header is string h && h == T(step.OpenTab));
        if (tab == null)
            return;
        tab.IsSelected = true;
        tabOpenedFor = step;
    }

    private void DetachTourClick()
    {
        if (tourTarget is Button button)
            button.RemoveHandler(Button.ClickEvent, TourTargetClicked);
    }

    private void TourTargetClicked(object? sender, RoutedEventArgs e) => Dispatcher.UIThread.Post(() => vm?.TourTargetClicked());

    // A tour target ("nav:binds", "name:X", "cmd:XCommand", "tab:Live test", "section:games", "page"): the first shown
    // control that fits, or null (the card then stands in the middle).
    public Control? FindTarget(string key)
    {
        if (content == null || string.IsNullOrWhiteSpace(key) || key == "none")
            return null;
        // "a|b": the first alternative that is shown (the helper's install card, else its status line).
        if (key.Contains('|', StringComparison.Ordinal))
        {
            foreach (string alternative in key.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (FindTarget(alternative) is { } found)
                    return found;
            }
            return null;
        }
        // "name:Card>Child": the control named Child inside the shown control the part before ">" finds (the license box of
        // the ViGEmBus card, not of the first component card on the page).
        Visual root = content;
        int inside = key.IndexOf('>', StringComparison.Ordinal);
        if (inside > 0)
        {
            if (FindTarget(key[..inside]) is not { } scope)
                return null;
            root = scope;
            key = "name:" + key[(inside + 1)..];
        }
        int colon = key.IndexOf(':');
        string kind = colon > 0 ? key[..colon] : key;
        string value = colon > 0 ? key[(colon + 1)..] : string.Empty;
        IEnumerable<Control> shown = root.GetVisualDescendants().OfType<Control>().Where(c => c.IsEffectivelyVisible && c.Bounds.Width > 0 && c.Bounds.Height > 0);
        Control? match = Match(kind, value, shown);
        if (match == null && kind is "name" or "cmd")
            OpenCollapsedPartsAround(Match(kind, value, root.GetVisualDescendants().OfType<Control>().Where(c => c.IsVisible)));
        return match;
    }

    private static Control? Match(string kind, string value, IEnumerable<Control> controls) => kind switch
    {
        "page" => controls.FirstOrDefault(c => c.Name == "PagesHost"),
        "nav" => controls.FirstOrDefault(c => c.Name == (value == "output" ? "EngineOutputQuickToggle" : "Nav_" + value)),
        "name" => controls.FirstOrDefault(c => c.Name == value),
        "cmd" => controls.OfType<Button>().FirstOrDefault(b => b.Command != null && HelpResolver.CommandName(b) == value),
        "tab" => controls.OfType<TabItem>().FirstOrDefault(t => t.Header is string h && h == T(value)),
        "section" => controls.OfType<SettingsSection>().FirstOrDefault(s => s.SectionId == value),
        _ => null
    };

    // A step's control inside a closed section (an Expander) of the open page: the section opens, the next look finds it.
    private static void OpenCollapsedPartsAround(Control? hidden)
    {
        if (hidden == null)
            return;
        List<Expander> closed = hidden.GetVisualAncestors().OfType<Expander>().Where(e => !e.IsExpanded).ToList();
        // Only when the closed sections are all that hides it (not a page, tab or panel that is hidden).
        if (closed.Count == 0 || !closed.All(e => e.IsEnabled) || hidden.GetVisualAncestors().OfType<Control>()
                .Any(a => !a.IsVisible && !closed.Any(e => ReferenceEquals(e, a) || a.GetVisualAncestors().Contains(e))))
            return;
        foreach (Expander expander in closed)
            expander.IsExpanded = true;
    }

    // ---- placing ----

    private void RequestPlace()
    {
        if (placePending || vm == null)
            return;
        // Nothing shown and nothing left to hide: no work at all (the layer costs nothing while help and tours are off).
        if (!vm.IsHelpModeOn && vm.HelpTopic == null && !vm.IsTourActive && !AnyMarkShown())
            return;
        placePending = true;
        Dispatcher.UIThread.Post(() =>
        {
            placePending = false;
            Place();
        }, DispatcherPriority.Render);
    }

    private bool AnyMarkShown() =>
        HoverOutline.IsVisible || HelpOutline.IsVisible || TourOutline.IsVisible || DimTop.IsVisible || DimBottom.IsVisible || DimLeft.IsVisible ||
        DimRight.IsVisible || TourCard.IsVisible;

    // Puts the outlines, dims and cards where their controls are now (after a scroll, a resize, a scale or page change).
    public void Place()
    {
        Size size = Bounds.Size;
        if (size.Width < 1 || size.Height < 1 || vm == null)
            return;

        Rect? hover = vm.IsHelpModeOn && hoverTarget != null && !ReferenceEquals(hoverTarget, helpTarget) ? RectOf(hoverTarget) : null;
        SetOutline(HoverOutline, hover, 3);

        Rect? help = vm.HelpTopic != null && helpTarget != null ? RectOf(helpTarget) : null;
        SetOutline(HelpOutline, help, 4);
        if (vm.HelpTopic != null)
            PlaceCard(HelpCard, help, size);

        bool tour = vm.IsTourActive && !vm.IsWelcomeOpen;
        if (tour && (tourTarget == null || !tourTarget.IsEffectivelyVisible || tourTarget.GetVisualRoot() == null))
        {
            FindTourTarget();
            if (tourTarget == null)
                ReportMissingTourTarget(tourStepVersion);
        }
        Rect? target = tour && tourTarget != null ? RectOf(tourTarget) : null;
        // Found, but outside the view (it was scrolled into view before its page had its size): once more per step.
        if (tour && tourTarget != null && target == null && !tourScrolledAgain)
        {
            tourScrolledAgain = true;
            tourTarget.BringIntoView();
            RequestPlace();
        }
        double pad = vm.TourIsBig ? 8 : 5;
        SetOutline(TourOutline, target, pad);
        TourOutline.BorderThickness = new Thickness(vm.TourIsBig ? 5 : 3);
        PlaceDims(tour, target, pad, size);
        TourCard.IsVisible = tour;
        if (tour)
            PlaceCard(TourCard, target, size);
        placedSignature = Signature();
    }

    private Rect? RectOf(Control control)
    {
        if (control.GetVisualRoot() == null || !control.IsEffectivelyVisible)
            return null;
        Point? origin = control.TranslatePoint(new Point(0, 0), this);
        if (origin == null)
            return null;
        Rect rect = new(origin.Value, control.Bounds.Size);
        Rect clipped = rect.Intersect(new Rect(Bounds.Size));
        // Only the part that shows: a control half scrolled out of its list is cut at the list's edge.
        for (Visual? v = control.GetVisualParent(); v != null && clipped.Width >= 1 && clipped.Height >= 1; v = v.GetVisualParent())
        {
            if (v is not Control { ClipToBounds: true } clip || clip.TranslatePoint(new Point(0, 0), this) is not Point at)
                continue;
            clipped = clipped.Intersect(new Rect(at, clip.Bounds.Size));
        }
        return clipped.Width < 1 || clipped.Height < 1 ? null : clipped;
    }

    private static void SetOutline(Border outline, Rect? rect, double pad)
    {
        outline.IsVisible = rect != null;
        if (rect is not Rect r)
            return;
        Canvas.SetLeft(outline, r.X - pad);
        Canvas.SetTop(outline, r.Y - pad);
        outline.Width = r.Width + 2 * pad;
        outline.Height = r.Height + 2 * pad;
    }

    private void PlaceDims(bool show, Rect? target, double pad, Size size)
    {
        foreach (Border dim in new[] { DimTop, DimBottom, DimLeft, DimRight })
            dim.IsVisible = show;
        if (!show)
            return;
        if (target is not Rect r)
        {
            // No control to point at: the whole window is dimmed, the card stands in the middle.
            Set(DimTop, 0, 0, size.Width, size.Height);
            DimBottom.IsVisible = DimLeft.IsVisible = DimRight.IsVisible = false;
            return;
        }
        Rect hole = new Rect(r.X - pad, r.Y - pad, r.Width + 2 * pad, r.Height + 2 * pad).Intersect(new Rect(size));
        Set(DimTop, 0, 0, size.Width, hole.Top);
        Set(DimBottom, 0, hole.Bottom, size.Width, size.Height - hole.Bottom);
        Set(DimLeft, 0, hole.Top, hole.Left, hole.Height);
        Set(DimRight, hole.Right, hole.Top, size.Width - hole.Right, hole.Height);

        static void Set(Border b, double x, double y, double w, double h)
        {
            Canvas.SetLeft(b, x);
            Canvas.SetTop(b, y);
            b.Width = Math.Max(0, w);
            b.Height = Math.Max(0, h);
        }
    }

    // A card next to its control: right, left, below or above, whichever fits first; always inside the window.
    private static void PlaceCard(Border card, Rect? target, Size size)
    {
        const double gap = 14, margin = 8;
        double maxWidth = Math.Max(200, Math.Min(card.MaxWidth, size.Width - 2 * margin));
        card.Measure(new Size(maxWidth, double.PositiveInfinity));
        Size want = card.DesiredSize;
        double w = Math.Min(want.Width, maxWidth), h = Math.Min(want.Height, size.Height - 2 * margin);
        card.MaxHeight = size.Height - 2 * margin;
        double x, y;
        if (target is not Rect r)
        {
            x = (size.Width - w) / 2;
            y = (size.Height - h) / 2;
        }
        else if (r.Width > size.Width * 0.6 && r.Height > size.Height * 0.45)
        {
            // A whole page: the card in its lower right corner, over the page (there is no room beside it).
            x = Math.Clamp(r.Right - w - 24, margin, Math.Max(margin, size.Width - w - margin));
            y = Math.Clamp(r.Bottom - h - 24, margin, Math.Max(margin, size.Height - h - margin));
        }
        else
        {
            (double X, double Y)[] tries =
            [
                (r.Right + gap, r.Top),
                (r.Left - gap - w, r.Top),
                (r.Left, r.Bottom + gap),
                (r.Left, r.Top - gap - h)
            ];
            (double X, double Y)? fit = null;
            foreach ((double tx, double ty) in tries)
            {
                double cy = Math.Clamp(ty, margin, Math.Max(margin, size.Height - h - margin));
                double cx = Math.Clamp(tx, margin, Math.Max(margin, size.Width - w - margin));
                Rect placed = new(cx, cy, w, h);
                if (!placed.Intersects(r.Inflate(4)))
                {
                    fit = (cx, cy);
                    break;
                }
            }
            if (fit == null)
            {
                // Nothing fits beside the control (a big control): below or above it where there is more room.
                double below = size.Height - r.Bottom, above = r.Top;
                double fy = below >= above ? Math.Min(r.Bottom + gap, size.Height - h - margin) : Math.Max(margin, r.Top - gap - h);
                fit = (Math.Clamp(r.Left, margin, Math.Max(margin, size.Width - w - margin)), Math.Max(margin, fy));
            }
            (x, y) = fit.Value;
        }
        Canvas.SetLeft(card, Math.Max(margin, x));
        Canvas.SetTop(card, Math.Max(margin, y));
    }
}
