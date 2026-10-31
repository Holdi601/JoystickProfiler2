using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;

namespace JoyPro.Gui.ViewModels;

// Where a HUD stick element's axes come from (2.5.0 HUD bug: the user's VKB grip on his VPforce Rhino base is a device
// of its own without X/Y; the gimbal read the grip's missing X/Y and lay in a corner). Per device widget X, Y and twist
// each have a source: Automatic (the default: the device's own axis while it gives a position, else the one of the base
// the device sits on: buttons from the grip, axes from the base) or a picked device and axis. The choice is kept in
// hud.json with the widget. Pairing a grip with its base: the same alias (Layout page: both named "Right stick"), else a
// stick-like device of the same hand with its own X and Y and fewer buttons (a base, an FFB base), the fewest first.
// Nothing here runs per frame: the sources are worked out when the devices or a choice change.
public sealed partial class MainWindowViewModel
{
    public const string HudAxisAutomatic = "";

    // The choices of the three boxes: Automatic, then every axis of every device on the HUD ("VPforce Rhino · X").
    public ObservableCollection<HudChoice> HudAxisChoices { get; } = [];

    public HudChoice? SelectedHudAxisX
    {
        get => HudAxisChoiceOf(HudDevice.RoleX);
        set => SetHudAxisChoice(HudDevice.RoleX, value);
    }

    public HudChoice? SelectedHudAxisY
    {
        get => HudAxisChoiceOf(HudDevice.RoleY);
        set => SetHudAxisChoice(HudDevice.RoleY, value);
    }

    public HudChoice? SelectedHudAxisTwist
    {
        get => HudAxisChoiceOf(HudDevice.RoleTwist);
        set => SetHudAxisChoice(HudDevice.RoleTwist, value);
    }

    // What Automatic uses for the selected widget ("Automatic: X and Y from VPforce Rhino FFB Joystick").
    public string HudAxisAutomaticText
    {
        get
        {
            HudDevice? device = SelectedHudWidget?.Device;
            if (device == null)
                return string.Empty;
            if (device.PairedBase is { } paired && (device.OwnIndex(HudDevice.RoleX) < 0 || device.OwnIndex(HudDevice.RoleY) < 0))
                return T("Automatic: this device has no X/Y of its own; they come from {0}.", paired.ShortTitle);
            if (device.PairedBase is { } other)
                return T("Automatic: the device's own axes; if they give no position, those of {0}.", other.ShortTitle);
            return device.HasGimbal
                ? T("Automatic: the device's own axes. A missing axis stays in the middle.")
                : T("Automatic: this device has no X/Y; pick the device and axis below (a missing axis stays in the middle).");
        }
    }

    private HudChoice? HudAxisChoiceOf(int role)
    {
        string id = SelectedHudWidget?.AxisSourceOf(role) ?? HudAxisAutomatic;
        return HudAxisChoices.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase)) ?? HudAxisChoices.FirstOrDefault();
    }

    private void SetHudAxisChoice(int role, HudChoice? value)
    {
        if (SelectedHudWidget is not { IsDevice: true } widget || value == null || string.Equals(widget.AxisSourceOf(role), value.Id, StringComparison.OrdinalIgnoreCase))
            return;
        widget.SetAxisSource(role, value.Id);
        JoyPro.Logging.JoyProLog.Info("Hud", "Axis source of " + widget.Config.Device + " (" + (role == HudDevice.RoleX ? "X" : role == HudDevice.RoleY ? "Y" : "twist") + "): " +
                                             (value.Id.Length == 0 ? "Automatic" : value.Id));
    }

    // The sources of every device widget, from the widgets' choices and the pairing (after the devices are read and
    // whenever a choice changes).
    internal void ApplyHudAxisSources()
    {
        List<HudWidget> widgets = HudWidgets.Where(w => w.Device != null).ToList();
        List<HudDevice> devices = widgets.Select(w => w.Device!).ToList();
        foreach (HudDevice device in devices)
            device.ClearAxisDependents();
        foreach (HudWidget widget in widgets)
        {
            HudDevice device = widget.Device!;
            device.SetAxisSources(PairedHudBase(device, devices), ResolveHudAxis(widget.AxisX, devices), ResolveHudAxis(widget.AxisY, devices),
                ResolveHudAxis(widget.AxisTwist, devices));
        }
        foreach (HudDevice device in devices)
        {
            foreach (HudDevice provider in device.AxisProviders())
                provider.AddAxisDependent(device);
            if (device.PairedBase != null)
                JoyPro.Logging.JoyProLog.Debug("Hud", device.Device + " sits on " + device.PairedBase.Device + " (axes it lacks come from there)");
        }
        foreach (HudWidget widget in widgets)
            widget.RefreshVisibility();
        RebuildHudAxisChoices(devices);
    }

    private void RebuildHudAxisChoices(List<HudDevice> devices)
    {
        HudAxisChoices.Clear();
        HudAxisChoices.Add(new HudChoice(HudAxisAutomatic, T("Automatic")));
        foreach (HudDevice device in devices)
        {
            foreach (string input in device.ReportedAxes())
                HudAxisChoices.Add(new HudChoice(device.Device + "|" + input, device.ShortTitle + " · " + input.Replace("JOY_", string.Empty)));
        }
        NotifyHudAxisSelection();
    }

    private void NotifyHudAxisSelection()
    {
        OnPropertyChanged(nameof(SelectedHudAxisX));
        OnPropertyChanged(nameof(SelectedHudAxisY));
        OnPropertyChanged(nameof(SelectedHudAxisTwist));
        OnPropertyChanged(nameof(HudAxisAutomaticText));
    }

    // "<device>|JOY_X" -> the device on the HUD and the axis index; (null, -1) for Automatic or a device not connected.
    internal static (HudDevice? Device, int Index) ResolveHudAxis(string? source, IReadOnlyList<HudDevice> devices)
    {
        string text = (source ?? string.Empty).Trim();
        int bar = text.LastIndexOf('|');
        if (bar <= 0)
            return (null, -1);
        string name = text[..bar];
        int index = HudDevice.AxisIndex(text[(bar + 1)..]);
        HudDevice? device = devices.FirstOrDefault(d => OverlayResolver.SameDevice(d.Device, name));
        return device == null || index < 0 ? (null, -1) : (device, index);
    }

    private static bool StickLike(HudDevice device) => device.DeviceKind is "stick" or "base" or "hotas" or "yoke" or "controller";

    // The base a grip sits on: only for a stick-like device; candidates are stick-like devices with their own X and Y,
    // fewer buttons than the grip and no other hand. The same alias wins, then a base by its kind, then the fewest
    // buttons. Null: none (the device's own axes, a missing one centred).
    internal static HudDevice? PairedHudBase(HudDevice grip, IReadOnlyList<HudDevice> devices)
    {
        if (!StickLike(grip))
            return null;
        string alias = DeviceAliases.Shown(grip.Device) ?? string.Empty;
        return devices
            .Where(b => !ReferenceEquals(b, grip) && StickLike(b) && b.OwnIndex(HudDevice.RoleX) >= 0 && b.OwnIndex(HudDevice.RoleY) >= 0 &&
                        b.ButtonCount < grip.ButtonCount && !OtherHand(grip.Hand, b.Hand))
            .OrderByDescending(b => alias.Length > 0 && string.Equals(DeviceAliases.Shown(b.Device), alias, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(b => b.DeviceKind == "base")
            .ThenBy(b => b.ButtonCount)
            .ThenBy(b => b.Device, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static bool OtherHand(string a, string b) =>
        a is "left" or "right" && b is "left" or "right" && !string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
