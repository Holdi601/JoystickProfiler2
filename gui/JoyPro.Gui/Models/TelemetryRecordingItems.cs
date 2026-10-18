using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using JoyPro.Gui.Localization;
using JoyPro.TelemetrySessions;

namespace JoyPro.Gui.Models;

// Settings > Telemetry recording: one game JoyPro can record (its own switch).
public sealed partial class TelemetryRecordingGameItem : ObservableObject
{
    public TelemetryRecordingGameItem(TelemetryRecordingGame game)
    {
        Id = game.Id;
        Title = game.Title;
        Transport = game.Transport;
        HasPosition = game.HasPosition;
        Setup = game.Setup;
        ProcessNames = game.ProcessNames;
    }

    public string Id { get; }
    public string Title { get; }
    public string Transport { get; }
    public bool HasPosition { get; }
    public string Setup { get; }
    public IReadOnlyList<string> ProcessNames { get; }

    // Raised when the user flips the switch (not while the settings load).
    public event Action<TelemetryRecordingGameItem>? Changed;

    internal bool Loading { get; set; }

    [ObservableProperty]
    private bool enabled = true;

    partial void OnEnabledChanged(bool value)
    {
        if (!Loading)
            Changed?.Invoke(this);
    }

    public string Details => T("Reads: {0}", Loc.Translate(Transport)) + " · " + (HasPosition ? T("Tacview export: yes") : T("Tacview export: no (no position)")) +
                             (Setup.Length > 0 ? " · " + T("Needs: {0}", Loc.Translate(Setup)) : string.Empty);
}

// A choice of a combo box (value + label; the label is translated where it is made).
public sealed record TelemetryChoice(double Value, string Label)
{
    public override string ToString() => Label;
}

// One recorded session in the list.
public sealed class TelemetrySessionItem
{
    public TelemetrySessionItem(TelemetrySessionInfo info)
    {
        Info = info;
    }

    public TelemetrySessionInfo Info { get; }
    public string Path => Info.Path;
    public string FileName => Info.FileName;
    public string Game => Info.GameTitle;

    public string Aircraft => Info.Aircraft.Count == 0 ? "-" : string.Join(", ", Info.Aircraft.Take(3)) + (Info.Aircraft.Count > 3 ? " …" : string.Empty);

    public string Map => Info.Maps.Count == 0 ? string.Empty : string.Join(", ", Info.Maps.Take(2));

    public string Date => Info.CreatedUtc == DateTime.MinValue ? "?" : Info.CreatedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    public string Duration => TelemetrySessionStore.FormatDuration(Info.Duration);

    public string Size => TelemetrySessionStore.FormatSize(Info.SizeBytes);

    public bool CanExportAcmi => Info.HasPosition && !Info.InUse;

    public bool CanChange => !Info.InUse;

    public string State => Info.InUse ? T("recording") : !Info.Complete ? T("unfinished") : string.Empty;

    public bool HasState => State.Length > 0;

    public string Tooltip => FileName + Environment.NewLine +
                             T("{0} frames, {1} events, {2} channels", Info.Frames.ToString("N0", CultureInfo.CurrentCulture), Info.Events.ToString("N0", CultureInfo.CurrentCulture), Info.Channels) +
                             (Map.Length > 0 ? Environment.NewLine + T("Map: {0}", Map) : string.Empty) +
                             (Info.EndReason.Length > 0 ? Environment.NewLine + T("End: {0}", Loc.Translate(Info.EndReason)) : string.Empty);
}

// One plotted channel of the timeline preview.
public sealed record TelemetryPreviewTrack(string Label, IReadOnlyList<double> Times, IReadOnlyList<double> Values, double Min, double Max, double Duration);
