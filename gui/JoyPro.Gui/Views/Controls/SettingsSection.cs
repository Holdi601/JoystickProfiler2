using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.VisualTree;
using JoyPro.Gui.Models;

namespace JoyPro.Gui.Views.Controls;

// One section of the Settings page: a header (drag handle, open/close, icon, title, its own buttons, a menu to move it)
// and the settings below it, shown while the section is open. Lives in a SettingsSectionsPanel, which gives it its
// SettingsSectionItem (by SectionId) and its place. Look: Styles/SettingsSection.axaml.
public class SettingsSection : ContentControl
{
    public static readonly StyledProperty<string> SectionIdProperty =
        AvaloniaProperty.Register<SettingsSection, string>(nameof(SectionId), string.Empty);

    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<SettingsSection, string>(nameof(Title), string.Empty);

    public static readonly StyledProperty<Geometry?> IconProperty =
        AvaloniaProperty.Register<SettingsSection, Geometry?>(nameof(Icon));

    // Buttons of the section shown in its header (e.g. "Detect again"), visible while it is open.
    public static readonly StyledProperty<object?> HeaderContentProperty =
        AvaloniaProperty.Register<SettingsSection, object?>(nameof(HeaderContent));

    public static readonly DirectProperty<SettingsSection, bool> IsOpenProperty =
        AvaloniaProperty.RegisterDirect<SettingsSection, bool>(nameof(IsOpen), s => s.IsOpen);

    public static readonly DirectProperty<SettingsSection, bool> IsDraggingProperty =
        AvaloniaProperty.RegisterDirect<SettingsSection, bool>(nameof(IsDragging), s => s.IsDragging);

    private SettingsSectionItem? item;
    private bool isOpen;
    private bool isDragging;
    private Control? grip;
    private Control? toggle;
    private Control? header;
    private Button? menu;

    public string SectionId
    {
        get => GetValue(SectionIdProperty);
        set => SetValue(SectionIdProperty, value);
    }

    public string Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public Geometry? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public object? HeaderContent
    {
        get => GetValue(HeaderContentProperty);
        set => SetValue(HeaderContentProperty, value);
    }

    public bool IsOpen
    {
        get => isOpen;
        private set => SetAndRaise(IsOpenProperty, ref isOpen, value);
    }

    public bool IsDragging
    {
        get => isDragging;
        internal set
        {
            SetAndRaise(IsDraggingProperty, ref isDragging, value);
            PseudoClasses.Set(":dragging", value);
        }
    }

    public SettingsSectionItem? Item
    {
        get => item;
        internal set
        {
            if (ReferenceEquals(item, value))
                return;
            if (item != null)
                item.PropertyChanged -= OnItemPropertyChanged;
            item = value;
            if (item != null)
            {
                item.PropertyChanged += OnItemPropertyChanged;
                if (string.IsNullOrEmpty(Title))
                    Title = T(item.Title);
            }
            Refresh();
        }
    }

    private SettingsSectionsPanel? Host => Parent as SettingsSectionsPanel;

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        IsOpen = item?.IsContentVisible ?? true;
        IsVisible = item?.MatchesSearch ?? true;
        PseudoClasses.Set(":open", IsOpen);
        if (toggle != null)
            ToolTip.SetTip(toggle, item?.ToggleTip ?? string.Empty);
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        if (grip != null)
        {
            grip.PointerPressed -= OnGripPressed;
            grip.PointerMoved -= OnGripMoved;
            grip.PointerReleased -= OnGripReleased;
            grip.PointerCaptureLost -= OnGripCaptureLost;
        }
        if (toggle != null)
            toggle.KeyDown -= OnToggleKeyDown;
        if (header != null)
        {
            header.RemoveHandler(PointerPressedEvent, OnHeaderPressed);
            header.RemoveHandler(PointerReleasedEvent, OnHeaderReleased);
        }
        header = e.NameScope.Find<Control>("PART_Header");
        // handledEventsToo: nothing inside the row (a PathIcon, a style, a control's own handler) can swallow the click.
        if (header != null)
        {
            header.AddHandler(PointerPressedEvent, OnHeaderPressed, RoutingStrategies.Bubble, handledEventsToo: true);
            header.AddHandler(PointerReleasedEvent, OnHeaderReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        }
        grip = e.NameScope.Find<Control>("PART_Grip");
        toggle = e.NameScope.Find<Control>("PART_Toggle");
        menu = e.NameScope.Find<Button>("PART_Menu");
        if (grip != null)
        {
            grip.PointerPressed += OnGripPressed;
            grip.PointerMoved += OnGripMoved;
            grip.PointerReleased += OnGripReleased;
            grip.PointerCaptureLost += OnGripCaptureLost;
        }
        if (toggle != null)
            toggle.KeyDown += OnToggleKeyDown;
        if (menu != null)
            menu.Flyout = BuildMenu();
        Refresh();
    }

    private Point? headerPress;

    // A press and a release on the row (the chevron, the icon, the title, the empty space; not its buttons, not the drag
    // handle, which toggles on its own release) open or close the section.
    private void OnHeaderPressed(object? sender, PointerPressedEventArgs e)
    {
        headerPress = null;
        if (header == null || !e.GetCurrentPoint(header).Properties.IsLeftButtonPressed || e.Source is not Visual source || !HeaderTapToggles(source, header))
            return;
        headerPress = e.GetPosition(header);
    }

    private void OnHeaderReleased(object? sender, PointerReleasedEventArgs e)
    {
        Point? press = headerPress;
        headerPress = null;
        if (press == null || header == null || e.InitialPressMouseButton != MouseButton.Left || e.Source is not Visual source || !HeaderTapToggles(source, header))
            return;
        Point at = e.GetPosition(header);
        if (!new Rect(header.Bounds.Size).Contains(at) || Math.Abs(at.X - press.Value.X) + Math.Abs(at.Y - press.Value.Y) > 12)
            return;
        Toggle();
        toggle?.Focus(NavigationMethod.Pointer);
        e.Handled = true;
    }

    private void OnToggleKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Space)
        {
            Toggle();
            e.Handled = true;
        }
    }

    // Inside the header: not on its own buttons, its menu or the drag handle.
    internal static bool HeaderTapToggles(Visual source, Control? header) =>
        header != null && !source.GetSelfAndVisualAncestors().TakeWhile(v => !ReferenceEquals(v, header))
            .Any(v => v is Button || v is Avalonia.Controls.Primitives.ToggleButton || (v is Control c && c.Name is "PART_Grip" or "PART_HeaderContent" or "PART_Menu") ||
                      v is InputElement { Focusable: true } and not Control { Name: "PART_Toggle" });

    internal void Toggle()
    {
        if (item != null)
            item.IsExpanded = !item.IsExpanded;
    }

    // The section's menu: move it (the same as dragging it), open or close all.
    private MenuFlyout BuildMenu()
    {
        MenuFlyout flyout = new() { Placement = PlacementMode.BottomEdgeAlignedRight };
        void Add(string header, Action<SettingsSectionsPanel, SettingsSection> action)
        {
            MenuItem entry = new() { Header = header };
            entry.Click += (_, _) =>
            {
                if (Host is { } host)
                    action(host, this);
            };
            flyout.Items.Add(entry);
        }
        Add(T("Move up"), (host, section) => host.MoveBy(section, -1));
        Add(T("Move down"), (host, section) => host.MoveBy(section, 1));
        Add(T("Move to the top"), (host, section) => host.MoveTo(section, 0));
        Add(T("Move to the bottom"), (host, section) => host.MoveTo(section, int.MaxValue));
        return flyout;
    }

    private void OnGripPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || Host is not { } host)
            return;
        e.Pointer.Capture(grip);
        gripPress = e.GetPosition(host);
        host.BeginDrag(this, gripPress.Value);
        e.Handled = true;
    }

    private Point? gripPress;

    private void OnGripMoved(object? sender, PointerEventArgs e)
    {
        if (IsDragging && Host is { } host)
            host.DragTo(e.GetPosition(host));
    }

    private void OnGripReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!IsDragging || Host is not { } host)
            return;
        // A click on the handle without moving: open or close, like the rest of the row.
        Point at = e.GetPosition(host);
        bool click = gripPress is { } press && Math.Abs(at.X - press.X) + Math.Abs(at.Y - press.Y) < 4;
        gripPress = null;
        host.EndDrag(commit: !click);
        e.Pointer.Capture(null);
        e.Handled = true;
        if (click)
            Toggle();
    }

    private void OnGripCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (IsDragging)
            Host?.EndDrag(commit: false);
    }

    // The words of the section's settings for the search: labels, check boxes, buttons, headers, tips, watermarks.
    public string CollectSearchText()
    {
        StringBuilder sb = new();
        void Add(object? value)
        {
            if (value is string text && text.Length > 0)
                sb.Append(' ').Append(text);
        }
        Add(Title);
        IEnumerable<ILogical> roots = new ILogical?[] { Content as ILogical, HeaderContent as ILogical }.Where(r => r != null)!;
        foreach (ILogical root in roots)
        {
            foreach (ILogical node in new[] { root }.Concat(root.GetLogicalDescendants()))
            {
                switch (node)
                {
                    case TextBlock block:
                        Add(block.Text);
                        break;
                    case TextBox box:
                        Add(box.Watermark);
                        break;
                    case HeaderedContentControl headered:
                        Add(headered.Header);
                        Add(headered.Content);
                        break;
                    case ContentControl content:
                        Add(content.Content);
                        break;
                }
                if (node is Control control)
                    Add(ToolTip.GetTip(control));
            }
        }
        return sb.ToString();
    }
}
