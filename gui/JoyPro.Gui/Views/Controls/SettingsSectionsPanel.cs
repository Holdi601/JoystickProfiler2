using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using JoyPro.Gui.Models;
using JoyPro.Gui.ViewModels;

namespace JoyPro.Gui.Views.Controls;

// The sections of the Settings page, stacked in the order of Sections (the view model's list, which the user changes
// by dragging a section's handle or a tab). The sections are declared once in MainWindow.axaml; this panel only decides
// where each one stands, so moving one never rebuilds the settings in it. While a section is dragged a line shows where
// it lands.
public sealed class SettingsSectionsPanel : Panel
{
    public static readonly StyledProperty<ObservableCollection<SettingsSectionItem>?> SectionsProperty =
        AvaloniaProperty.Register<SettingsSectionsPanel, ObservableCollection<SettingsSectionItem>?>(nameof(Sections));

    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<SettingsSectionsPanel, double>(nameof(Spacing), 8);

    private ObservableCollection<SettingsSectionItem>? observed;
    private SettingsSection? dragged;
    private int dropIndex = -1;
    private Point dragStart;
    private bool dragMoved;

    // Where a dragged section lands (shown only while dragging).
    private readonly Border dropLine = new() { Height = 3, CornerRadius = new CornerRadius(1.5), IsHitTestVisible = false, IsVisible = false, ZIndex = 100 };

    static SettingsSectionsPanel()
    {
        AffectsMeasure<SettingsSectionsPanel>(SpacingProperty);
    }

    public SettingsSectionsPanel()
    {
        dropLine.Bind(Border.BackgroundProperty, dropLine.GetResourceObservable("JP.AccentBrush"));
        Children.Add(dropLine);
    }

    public ObservableCollection<SettingsSectionItem>? Sections
    {
        get => GetValue(SectionsProperty);
        set => SetValue(SectionsProperty, value);
    }

    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SectionsProperty)
            Observe(VisualRoot != null ? Sections : null);
    }

    protected override void ChildrenChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        base.ChildrenChanged(sender, e);
        AssignItems();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Observe(Sections);
        JoyPro.Gui.Localization.Loc.LanguageChanged += OnLanguageChanged;
        // Once the settings are built and bound: their texts for the search.
        Dispatcher.UIThread.Post(ReadSectionTexts, DispatcherPriority.Background);
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(ReadSectionTexts, DispatcherPriority.Background);

    // The window goes away: the sections let go of the view model's items (they outlive the window).
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        JoyPro.Gui.Localization.Loc.LanguageChanged -= OnLanguageChanged;
        Observe(null);
    }

    private void Observe(ObservableCollection<SettingsSectionItem>? sections)
    {
        if (!ReferenceEquals(observed, sections))
        {
            if (observed != null)
                observed.CollectionChanged -= OnSectionsChanged;
            observed = sections;
            if (observed != null)
                observed.CollectionChanged += OnSectionsChanged;
        }
        AssignItems();
        InvalidateMeasure();
    }

    private void OnSectionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        AssignItems();
        InvalidateMeasure();
    }

    private void AssignItems()
    {
        foreach (SettingsSection section in Children.OfType<SettingsSection>())
            section.Item = observed?.FirstOrDefault(s => string.Equals(s.Id, section.SectionId, StringComparison.OrdinalIgnoreCase));
    }

    private void ReadSectionTexts()
    {
        foreach (SettingsSection section in Children.OfType<SettingsSection>())
        {
            if (section.Item != null)
                section.Item.ViewText = section.CollectSearchText();
        }
        (DataContext as MainWindowViewModel)?.SettingsSectionTextsRead();
    }

    // The children in the order of Sections; children without a section item keep their place at the end.
    internal List<Control> OrderedChildren()
    {
        List<Control> children = Children.Where(c => !ReferenceEquals(c, dropLine)).ToList();
        if (Sections == null)
            return children;
        return children
            .Select((child, index) => (child, index, order: child is SettingsSection { Item: { } item } ? Sections.IndexOf(item) : -1))
            .OrderBy(c => c.order < 0 ? int.MaxValue : c.order)
            .ThenBy(c => c.index)
            .Select(c => c.child)
            .ToList();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = 0;
        double height = 0;
        bool first = true;
        Size childAvailable = new(availableSize.Width, double.PositiveInfinity);
        dropLine.Measure(childAvailable);
        foreach (Control child in OrderedChildren())
        {
            child.Measure(childAvailable);
            if (!child.IsVisible)
                continue;
            if (!first)
                height += Spacing;
            first = false;
            width = Math.Max(width, child.DesiredSize.Width);
            height += child.DesiredSize.Height;
        }
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double y = 0;
        bool first = true;
        foreach (Control child in OrderedChildren())
        {
            if (!child.IsVisible)
            {
                child.Arrange(new Rect(0, y, finalSize.Width, 0));
                continue;
            }
            if (!first)
                y += Spacing;
            first = false;
            child.Arrange(new Rect(0, y, finalSize.Width, child.DesiredSize.Height));
            y += child.DesiredSize.Height;
        }
        ArrangeDropLine(finalSize.Width);
        // The whole slot: a smaller size makes the layout center the panel in the scroll viewer (a search that leaves one
        // short section showed it hundreds of pixels under the tabs).
        return new Size(finalSize.Width, Math.Max(y, finalSize.Height));
    }

    private void ArrangeDropLine(double width)
    {
        dropLine.IsVisible = dragged != null && dragMoved && dropIndex >= 0;
        if (!dropLine.IsVisible)
            return;
        List<SettingsSection> visible = VisibleSections();
        double y = dropIndex < visible.Count
            ? visible[dropIndex].Bounds.Y - Spacing / 2
            : (visible.Count > 0 ? visible[^1].Bounds.Bottom + Spacing / 2 : 0);
        dropLine.Arrange(new Rect(0, Math.Max(0, y - 1.5), width, 3));
    }

    // ---- moving ----

    private List<SettingsSection> VisibleSections() => OrderedChildren().OfType<SettingsSection>().Where(s => s.IsVisible && s.Item != null).ToList();

    internal void BeginDrag(SettingsSection section, Point position)
    {
        dragged = section;
        dragStart = position;
        dragMoved = false;
        dropIndex = -1;
        section.IsDragging = true;
    }

    internal void DragTo(Point position)
    {
        if (dragged == null)
            return;
        if (!dragMoved && Math.Abs(position.Y - dragStart.Y) < 4)
            return;
        dragMoved = true;
        List<SettingsSection> visible = VisibleSections();
        int index = visible.Count;
        for (int i = 0; i < visible.Count; i++)
        {
            Rect bounds = visible[i].Bounds;
            if (position.Y < bounds.Y + bounds.Height / 2)
            {
                index = i;
                break;
            }
        }
        if (index != dropIndex)
        {
            dropIndex = index;
            ArrangeDropLine(Bounds.Width);
        }
    }

    internal void EndDrag(bool commit)
    {
        SettingsSection? section = dragged;
        int index = dropIndex;
        dragged = null;
        dropIndex = -1;
        if (section == null)
            return;
        section.IsDragging = false;
        ArrangeDropLine(Bounds.Width);
        if (!commit || !dragMoved || index < 0 || section.Item == null)
            return;
        List<SettingsSection> visible = VisibleSections();
        // Dropped before the visible section at index (or after the last one).
        SettingsSectionItem? before = index < visible.Count ? visible[index].Item : null;
        MoveBefore(section, before);
    }

    private void MoveBefore(SettingsSection section, SettingsSectionItem? before)
    {
        if (Sections == null || section.Item == null || ReferenceEquals(before, section.Item))
            return;
        int from = Sections.IndexOf(section.Item);
        int to = before == null ? Sections.Count - 1 : Sections.IndexOf(before);
        if (from < 0 || to < 0)
            return;
        if (before != null && from < to)
            to--;
        Move(section.Item, to);
    }

    internal void MoveBy(SettingsSection section, int delta)
    {
        if (Sections == null || section.Item == null)
            return;
        // Past the sections the search hides.
        List<SettingsSection> visible = VisibleSections();
        int at = visible.IndexOf(section);
        int target = at + delta;
        if (at < 0 || target < 0 || target >= visible.Count)
            return;
        Move(section.Item, Sections.IndexOf(visible[target].Item!));
    }

    internal void MoveTo(SettingsSection section, int index)
    {
        if (Sections == null || section.Item == null)
            return;
        Move(section.Item, Math.Clamp(index, 0, Sections.Count - 1));
    }

    private void Move(SettingsSectionItem item, int to)
    {
        if (DataContext is MainWindowViewModel vm && ReferenceEquals(vm.SettingsSections, Sections))
            vm.MoveSettingsSection(item, to);
        else if (Sections != null && Sections.IndexOf(item) is int from and >= 0 && from != to)
            Sections.Move(from, to);
    }
}
