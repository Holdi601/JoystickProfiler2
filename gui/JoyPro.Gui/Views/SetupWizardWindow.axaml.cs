using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using JoyPro.Gui.Models;
using JoyPro.Gui.ViewModels;

namespace JoyPro.Gui.Views;

// The Setup wizard's window (SetupWizardViewModel). The priority list takes drag and drop: an aircraft onto a row (it
// counts the same as the aircraft there) or onto "new row", a row onto another row (it takes its place).
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public partial class SetupWizardWindow : Window
{
    private const string DragFormat = "joypro-setup-priority";
    private object? dragItem;
    private Point dragStart;
    private ulong aircraftPress;

    public SetupWizardWindow()
    {
        InitializeComponent();
        AddHandler(PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, (_, _) => dragItem = null, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private SetupWizardViewModel? Wizard => DataContext as SetupWizardViewModel;

    private void AircraftPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not SetupAircraftItem plane || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        dragItem = plane;
        dragStart = e.GetPosition(this);
        aircraftPress = e.Timestamp;
    }

    private void TierPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // A press on an aircraft chip inside the row drags the aircraft, not the row.
        if (e.Timestamp == aircraftPress || (sender as Control)?.DataContext is not SetupTierItem tier || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        dragItem = tier;
        dragStart = e.GetPosition(this);
    }

    private async void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (dragItem == null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        Point now = e.GetPosition(this);
        if (Math.Abs(now.X - dragStart.X) + Math.Abs(now.Y - dragStart.Y) < 6)
            return;
        object item = dragItem;
        dragItem = null;
        draggedItem = item;
#pragma warning disable CS0618 // DataObject / DoDragDrop: the Avalonia 11 drag data API
        DataObject data = new();
        data.Set(DragFormat, "1");
        await DragDrop.DoDragDrop(e, data, DragDropEffects.Move);
#pragma warning restore CS0618
        draggedItem = null;
    }

    private object? draggedItem;

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = draggedItem != null && (TargetTier(e.Source) != null || IsNewRowTarget(e.Source)) ? DragDropEffects.Move : DragDropEffects.None;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (Wizard == null || draggedItem == null)
            return;
        SetupTierItem? target = TargetTier(e.Source);
        if (draggedItem is SetupAircraftItem plane && (target != null || IsNewRowTarget(e.Source)))
            Wizard.DropAircraft(plane, target);
        else if (draggedItem is SetupTierItem tier && target != null)
            Wizard.MoveTierTo(tier, target);
        draggedItem = null;
    }

    private static SetupTierItem? TargetTier(object? source)
    {
        for (Visual? visual = source as Visual; visual != null; visual = visual.GetVisualParent())
        {
            if (visual is Control { DataContext: SetupTierItem tier })
                return tier;
        }
        return null;
    }

    private static bool IsNewRowTarget(object? source)
    {
        for (Visual? visual = source as Visual; visual != null; visual = visual.GetVisualParent())
        {
            if (visual is Control { Name: "NewTierDrop" })
                return true;
        }
        return false;
    }

    private void CloseClicked(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
