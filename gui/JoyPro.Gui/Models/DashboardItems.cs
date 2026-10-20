using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Media;
using JoyPro.Gui.Services;

namespace JoyPro.Gui.Models;

// One suggestion of the Start page's "Next steps": what to do now, why, and the button that does it.
public sealed class NextStepItem
{
    public NextStepItem(string title, string detail, string iconKey, string actionLabel, ICommand? command, object? commandParameter = null, bool isPrimary = false)
    {
        Title = title;
        Detail = detail;
        IconKey = iconKey;
        ActionLabel = actionLabel;
        Command = command;
        CommandParameter = commandParameter;
        IsPrimary = isPrimary;
    }

    public string Title { get; }

    public string Detail { get; }

    public string IconKey { get; }

    // Looked up when the page shows it (on the UI thread), not when the view model builds the step.
    public Geometry? Icon => DisplayText.Icon(IconKey);

    public string ActionLabel { get; }

    public ICommand? Command { get; }

    public object? CommandParameter { get; }

    public bool IsPrimary { get; }
}

// One line of the Start page's "Recent activity": the last export / import / accepted change JoyPro recorded for a
// game (engine sync status: RecordedUtc and Reason), or a game changed its controls since.
public sealed class ActivityItem
{
    public ActivityItem(string gameId, string game, string what, DateTime? whenUtc, bool changedSince)
    {
        GameId = gameId;
        Game = game;
        What = what;
        WhenUtc = whenUtc;
        ChangedSince = changedSince;
    }

    public string GameId { get; }

    public string Game { get; }

    public string What { get; }

    public DateTime? WhenUtc { get; }

    public bool ChangedSince { get; }

    public string WhenText => WhenUtc.HasValue ? DisplayText.When(WhenUtc.Value) : string.Empty;

    public string ChangedText => ChangedSince ? T("Changed in the game since") : string.Empty;
}

// Short display texts for the chips and badges of the pages (device names without the instance GUID, game ids as
// names, comma lists as chip lists).
public static class DisplayText
{
    // "Joystick - HOTAS Warthog {0000...}" -> "Joystick - HOTAS Warthog", or the device's alias ("Left panel") while
    // Settings shows aliases (Services/DeviceAliases). Every view shows device names through this.
    public static string ShortDevice(string device)
    {
        string alias = DeviceAliases.Shown(device);
        return alias.Length > 0 ? alias : RealShortDevice(device);
    }

    // The device's real name without the instance GUID (never the alias).
    public static string RealShortDevice(string device)
    {
        string text = (device ?? string.Empty).Trim();
        if (text.Length == 0 || text.Equals("<unbound>", StringComparison.OrdinalIgnoreCase) || text.Equals("<unknown>", StringComparison.OrdinalIgnoreCase))
            return string.Empty;
        int guid = text.LastIndexOf(" {", StringComparison.Ordinal);
        return guid > 0 && text.EndsWith('}') ? text[..guid] : text;
    }

    // The tooltip of a shown device name: "Left panel - alias of VPC Panel #2 {GUID}", else the full device name.
    public static string DeviceTip(string device)
    {
        string full = (device ?? string.Empty).Trim();
        string alias = DeviceAliases.Shown(full);
        return alias.Length > 0 ? T("{0} - alias of {1}", alias, full) : full;
    }

    // An input for a key cap ("JOY_BTN5"); empty for unbound.
    public static string Input(string input)
    {
        string text = (input ?? string.Empty).Trim();
        return text.Length == 0 || text.StartsWith('<') ? string.Empty : text;
    }

    // A bound input with its device and modifiers: "WarBRD · BTN 8 · Shift A" (the device without its instance id, the
    // input as on the key caps, the modifiers held with it). Empty for unbound.
    public static string BindLabel(string device, string input, IEnumerable<string>? modifiers)
    {
        string key = Input(input);
        if (key.Length == 0)
            return string.Empty;
        List<string> parts = [];
        string name = ShortDevice(device);
        if (name.Length > 0)
            parts.Add(name);
        parts.Add(DeviceMarkerText.ShortLabel(key));
        string mods = string.Join(" + ", (modifiers ?? []).Select(m => (m ?? string.Empty).Trim()).Where(m => m.Length > 0 && !m.StartsWith('<')));
        if (mods.Length > 0)
            parts.Add(mods);
        return string.Join(" · ", parts);
    }

    // The same with the full device name, for a tooltip: "VKB Gladiator WarBRD {GUID}: JOY_BTN8, hold Shift A".
    public static string BindTooltip(string device, string input, IEnumerable<string>? modifiers)
    {
        string key = Input(input);
        if (key.Length == 0)
            return string.Empty;
        string mods = string.Join(" + ", (modifiers ?? []).Select(m => (m ?? string.Empty).Trim()).Where(m => m.Length > 0 && !m.StartsWith('<')));
        string name = DeviceTip(device);
        return T("Device: {0}", name.Length > 0 ? name : "?") + "\n" + T("Input: {0} ({1})", key, DeviceMarkerText.ShortLabel(key)) + "\n" +
               (mods.Length > 0 ? T("Modifiers: hold {0}", mods) : T("No modifier"));
    }

    // A game id as people say it (the badge's short name: "WarThunder" -> "War Thunder").
    public static string Game(string gameId)
    {
        string id = (gameId ?? string.Empty).Trim();
        if (id.Length == 0)
            return string.Empty;
        try
        {
            string name = Views.Controls.GameBadge.StyleOf(id, string.Empty).Short;
            return string.IsNullOrWhiteSpace(name) ? id : name.Replace('\n', ' ');
        }
        catch (Exception)
        {
            return id;
        }
    }

    // "a, b, c" -> [a, b, c] without the placeholders the engine prints for none.
    public static IReadOnlyList<string> List(string csv, char separator = ',')
    {
        return (csv ?? string.Empty)
            .Split(separator, StringSplitOptions.RemoveEmptyEntries)
            .Select(v => v.Trim())
            .Where(v => v.Length > 0 && !v.StartsWith('<'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string When(DateTime utc)
    {
        DateTime local = utc.Kind == DateTimeKind.Local ? utc : DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime();
        TimeSpan ago = DateTime.Now - local;
        if (ago.TotalMinutes < 1)
            return "just now";
        if (ago.TotalHours < 1)
            return (int)ago.TotalMinutes + " min ago";
        if (local.Date == DateTime.Today)
            return "today " + local.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        if (local.Date == DateTime.Today.AddDays(-1))
            return "yesterday " + local.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        return local.ToString("d MMM HH:mm", System.Globalization.CultureInfo.InvariantCulture);
    }

    // The icon geometry of Styles/Icons.axaml (null without an application, as in the view model tests).
    public static Geometry? Icon(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || Avalonia.Application.Current == null || !Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            return null;
        return Avalonia.Application.Current.TryFindResource(key, out object? value) ? value as Geometry : null;
    }
}
