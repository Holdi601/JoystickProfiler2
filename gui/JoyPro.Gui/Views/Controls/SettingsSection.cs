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
    private Button? toggle;
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
            toggle.Click -= OnToggleClick;
        if (header != null)
            header.Tapped -= OnHeaderTapped;
        header = e.NameScope.Find<Control>("PART_Header");
        if (header != null)
            header.Tapped += OnHeaderTapped;
        grip = e.NameScope.Find<Control>("PART_Grip");
        toggle = e.NameScope.Find<Button>("PART_Toggle");
        menu = e.NameScope.Find<Button>("PART_Menu");
        if (grip != null)
        {
            grip.PointerPressed += OnGripPressed;
            grip.PointerMoved += OnGripMoved;
            grip.PointerReleased += OnGripReleased;
            grip.PointerCaptureLost += OnGripCaptureLost;
        }
        if (toggle != null)
            toggle.Click += OnToggleClick;
        if (menu != null)
            menu.Flyout = BuildMenu();
        Refresh();
    }

    // The header row outside its buttons and the drag handle (the empty space right of the title) toggles too.
    private void OnHeaderTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Visual source && HeaderTapToggles(source, header))
            Toggle();
    }

    internal static bool HeaderTapToggles(Visual source, Control? header) =>
        header != null && !source.GetSelfAndVisualAncestors().TakeWhile(v => !ReferenceEquals(v, header))
            .Any(v => v is Button || v is Avalonia.Controls.Primitives.ToggleButton || (v is Control c && c.Name == "PART_Grip") || v is InputElement { Focusable: true });

    internal void Toggle()
    {
        if (item != null)
            item.IsExpanded = !item.IsExpanded;
    }

    private void OnToggleClick(object? sender, RoutedEventArgs e)
    {
        if (item != null)
            item.IsExpanded = !item.IsExpanded;
        e.Handled = true;
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
        host.BeginDrag(this, e.GetPosition(host));
        e.Handled = true;
    }

    private void OnGripMoved(object? sender, PointerEventArgs e)
    {
        if (IsDragging && Host is { } host)
            host.DragTo(e.GetPosition(host));
    }

    private void OnGripReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!IsDragging || Host is not { } host)
            return;
        host.EndDrag(commit: true);
        e.Pointer.Capture(null);
        e.Handled = true;
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
