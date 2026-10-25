using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using JoyPro.Gui.Models;
using JoyPro.Gui.ViewModels;

namespace JoyPro.Gui.Views.Controls;

// The tabs at the top of the Settings page, one per section, in the sections' order: a click opens or closes the
// section (opening scrolls to it), a double click shows only that section; dragging a tab onto another moves the
// section there (the same list as the section handles). An open section's tab is highlighted, a section the search
// hides is dimmed.
public sealed class SettingsTabStrip : WrapPanel
{
    public static readonly StyledProperty<ObservableCollection<SettingsSectionItem>?> SectionsProperty =
        AvaloniaProperty.Register<SettingsTabStrip, ObservableCollection<SettingsSectionItem>?>(nameof(Sections));

    private ObservableCollection<SettingsSectionItem>? observed;
    private Border? pressed;
    private Point pressPoint;
    private int pressClicks;
    private bool dragging;
    private Border? dropTarget;
    private bool dropAfter;

    public SettingsTabStrip()
    {
        ItemSpacing = 6;
        LineSpacing = 6;
    }

    public ObservableCollection<SettingsSectionItem>? Sections
    {
        get => GetValue(SectionsProperty);
        set => SetValue(SectionsProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SectionsProperty)
            Observe(VisualRoot != null ? Sections : null);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        JoyPro.Gui.Localization.Loc.LanguageChanged += OnLanguageChanged;
        Observe(Sections);
    }

    // The tabs' names in the new language.
    private void OnLanguageChanged(object? sender, EventArgs e) => Avalonia.Threading.Dispatcher.UIThread.Post(Rebuild);

    // The window goes away: stop listening to the view model's items (they outlive the window).
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        JoyPro.Gui.Localization.Loc.LanguageChanged -= OnLanguageChanged;
        Observe(null);
    }

    private void Observe(ObservableCollection<SettingsSectionItem>? sections)
    {
        if (ReferenceEquals(observed, sections) && sections != null)
            return;
        if (observed != null)
        {
            observed.CollectionChanged -= OnSectionsChanged;
            foreach (SettingsSectionItem item in observed)
                item.PropertyChanged -= OnItemChanged;
        }
        observed = sections;
        if (observed != null)
        {
            observed.CollectionChanged += OnSectionsChanged;
            foreach (SettingsSectionItem item in observed)
                item.PropertyChanged += OnItemChanged;
        }
        Rebuild();
    }

    private void OnSectionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (SettingsSectionItem item in e.OldItems?.OfType<SettingsSectionItem>() ?? [])
            item.PropertyChanged -= OnItemChanged;
        foreach (SettingsSectionItem item in e.NewItems?.OfType<SettingsSectionItem>() ?? [])
            item.PropertyChanged += OnItemChanged;
        Rebuild();
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is SettingsSectionItem item && Children.OfType<Border>().FirstOrDefault(c => ReferenceEquals(c.Tag, item)) is { } tab)
            Style(tab, item);
    }

    private void Rebuild()
    {
        Children.Clear();
        if (observed == null)
            return;
        foreach (SettingsSectionItem item in observed)
        {
            Border tab = new()
            {
                Tag = item,
                Padding = new Thickness(10, 4),
                CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(1),
                Cursor = new Cursor(StandardCursorType.Hand),
                Child = ThemedText(T(item.TabTitle)),
            };
            ToolTip.SetTip(tab, T("{0} - Click: open/close - Double-click: show only this section - Drag: move it", T(item.Title)));
            tab.PointerPressed += OnTabPressed;
            tab.PointerMoved += OnTabMoved;
            tab.PointerReleased += OnTabReleased;
            tab.PointerCaptureLost += (_, _) => CancelDrag();
            Style(tab, item);
            Children.Add(tab);
        }
    }

    // The theme's brushes as dynamic resources, so the tabs follow a theme change.
    private static void Style(Border tab, SettingsSectionItem item, bool dropTarget = false)
    {
        bool open = item.IsContentVisible;
        tab.Bind(Border.BackgroundProperty, tab.GetResourceObservable(open ? "JP.AccentSoftBrush" : "JP.SurfaceRaisedBrush"));
        tab.Bind(Border.BorderBrushProperty, tab.GetResourceObservable(open || dropTarget ? "JP.AccentBrush" : "JP.BorderBrush"));
        tab.Opacity = item.MatchesSearch ? 1 : 0.4;
        tab.BorderThickness = new Thickness(1);
        if (tab.Child is TextBlock text)
        {
            text.Bind(TextBlock.ForegroundProperty, text.GetResourceObservable(open ? "JP.AccentTextBrush" : "JP.TextBrush"));
            text.FontWeight = open ? FontWeight.SemiBold : FontWeight.Normal;
        }
    }

    private void OnTabPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border tab || !e.GetCurrentPoint(tab).Properties.IsLeftButtonPressed)
            return;
        pressed = tab;
        pressPoint = e.GetPosition(this);
        pressClicks = e.ClickCount;
        dragging = false;
        e.Pointer.Capture(tab);
        e.Handled = true;
    }

    private void OnTabMoved(object? sender, PointerEventArgs e)
    {
        if (pressed == null)
            return;
        Point p = e.GetPosition(this);
        if (!dragging && Math.Abs(p.X - pressPoint.X) + Math.Abs(p.Y - pressPoint.Y) < 6)
            return;
        dragging = true;
        pressed.Opacity = 0.5;
        Border? target = Children.OfType<Border>().FirstOrDefault(c => c.Bounds.Inflate(3).Contains(p));
        bool after = target != null && p.X > target.Bounds.X + target.Bounds.Width / 2;
        if (!ReferenceEquals(target, dropTarget) || after != dropAfter)
        {
            if (dropTarget?.Tag is SettingsSectionItem previous)
                Style(dropTarget, previous);
            dropTarget = target;
            dropAfter = after;
            if (target is { Tag: SettingsSectionItem onto } && !ReferenceEquals(target, pressed))
            {
                Style(target, onto, dropTarget: true);
                target.BorderThickness = after ? new Thickness(1, 1, 4, 1) : new Thickness(4, 1, 1, 1);
            }
        }
    }

    private void OnTabReleased(object? sender, PointerReleasedEventArgs e)
    {
        Border? tab = pressed;
        bool wasDrag = dragging;
        int clicks = pressClicks;
        Border? target = dropTarget;
        bool after = dropAfter;
        CancelDrag();
        e.Pointer.Capture(null);
        if (tab?.Tag is not SettingsSectionItem item || DataContext is not MainWindowViewModel vm)
            return;
        e.Handled = true;
        if (!wasDrag)
        {
            // The first click of a double click opened or closed it; the second leaves only this section open.
            if (clicks >= 2)
                vm.ShowOnlySettingsSection(item);
            else
                vm.ToggleSettingsSectionFromTab(item);
            return;
        }
        if (target?.Tag is not SettingsSectionItem onto || ReferenceEquals(onto, item))
            return;
        int from = vm.SettingsSections.IndexOf(item);
        int to = vm.SettingsSections.IndexOf(onto) + (after ? 1 : 0);
        if (from < to)
            to--;
        vm.MoveSettingsSection(item, to);
    }

    private void CancelDrag()
    {
        if (pressed?.Tag is SettingsSectionItem item)
            Style(pressed, item);
        if (dropTarget?.Tag is SettingsSectionItem target)
            Style(dropTarget, target);
        pressed = null;
        dragging = false;
        dropTarget = null;
    }

    // A tab's caption at the theme's text size (Settings > Appearance).
    private static TextBlock ThemedText(string text)
    {
        TextBlock block = new() { Text = text, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 260, TextTrimming = TextTrimming.CharacterEllipsis };
        block.Bind(TextBlock.FontSizeProperty, block.GetResourceObservable("JP.FontSize.12"));
        return block;
    }
}
