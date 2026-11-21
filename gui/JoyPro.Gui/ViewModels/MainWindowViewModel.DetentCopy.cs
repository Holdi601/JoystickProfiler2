using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JoyPro.Gui.Models;

namespace JoyPro.Gui.ViewModels;

// Copy detent mode to another axis (2.7.1 tester report: "is it not possible to copy detent mode to another axis? The
// button is greyed out"): the curve's Copy to another axis copies a curve, and the curve detent mode makes cannot be
// copied as one (it would win over detent mode there). This copies detent mode itself: on/off, the rule for aircraft
// without afterburner, the margin and the relation's own afterburner values (binds detent copy <relation> --to <other>,
// one engine run, one Ctrl+Z). The calibration belongs to the device axis; ticked, the other axis borrows this one's
// until it is calibrated itself.
public sealed partial class MainWindowViewModel
{
    public ObservableCollection<AxisTuningCopyTarget> DetentCopyTargets { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCopyDetent))]
    private AxisTuningCopyTarget? detentCopyTarget;

    // The other axis borrows this axis's calibration when it has none yet.
    [ObservableProperty]
    private bool detentCopyCalibration = true;

    public bool HasDetentCopyTargets => DetentCopyTargets.Count > 0;

    public bool CanPrepareDetentCopy => DetentLoaded && detentStored && detentRelation.Length > 0;

    public bool CanCopyDetent => CanPrepareDetentCopy && DetentCopyTarget != null;

    partial void OnDetentLoadedChanged(bool value)
    {
        OnPropertyChanged(nameof(CanPrepareDetentCopy));
        OnPropertyChanged(nameof(CanCopyDetent));
    }

    // The other bound axes, picked again each time the copy opens; Thrust Right offers Thrust Left first.
    [RelayCommand]
    private void PrepareDetentCopy()
    {
        string current = detentRelation;
        string? previous = DetentCopyTarget?.Relation;
        List<AxisTuningCopyTarget> targets = Binds
            .Where(b => b.IsAxisType && b.HasBind && !string.Equals(b.RelationName, current, StringComparison.OrdinalIgnoreCase))
            .GroupBy(b => b.RelationName, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(b => b.RelationName, StringComparer.OrdinalIgnoreCase)
            .Select(b => new AxisTuningCopyTarget(b.RelationName, string.Join(" · ", new[] { b.RelationName, b.DeviceShort, b.InputLabel }.Where(t => !string.IsNullOrWhiteSpace(t)))))
            .ToList();
        DetentCopyTargets.Clear();
        foreach (AxisTuningCopyTarget target in targets)
            DetentCopyTargets.Add(target);
        DetentCopyTarget = targets.FirstOrDefault(t => string.Equals(t.Relation, previous, StringComparison.OrdinalIgnoreCase)) ?? BestCopyTarget(current, targets);
        OnPropertyChanged(nameof(HasDetentCopyTargets));
        OnPropertyChanged(nameof(CanCopyDetent));
    }

    internal List<string> BuildDetentCopyArguments(string target)
    {
        List<string> args = ["binds", "detent", "copy", detentRelation, "--to", target];
        if (DetentCopyCalibration)
            args.Add("--calibration");
        return args;
    }

    [RelayCommand]
    private async Task CopyDetentToAxisAsync()
    {
        AxisTuningCopyTarget? target = DetentCopyTarget;
        if (target == null || !CanPrepareDetentCopy)
            return;
        string from = detentRelation;
        EngineCommandResult? result = await RunEngineAsync(BuildDetentCopyArguments(target.Relation), includeProfile: true, mutatesProfile: true,
            busyLabel: T("Copying detent mode..."), forceText: false);
        if (result?.ExitCode != 0)
            return;
        StatusText = DescribeDetentCopy(from, target.Relation, result.StandardOutput);
    }

    // "Detent mode of 'Thrust Right' is copied to 'Thrust Left'. ..." with what the other axis still needs.
    internal string DescribeDetentCopy(string from, string to, string output)
    {
        List<string> parts = [T("Detent mode of '{0}' is copied to '{1}'. Ctrl+Z undoes it.", from, to)];
        try
        {
            using JsonDocument doc = JsonDocument.Parse(ExtractJsonPayload(output));
            JsonElement root = doc.RootElement;
            if (!root.TryGetProperty("Calibration", out JsonElement cal) || cal.ValueKind != JsonValueKind.Object)
                parts.Add(T("Its axis is not calibrated yet: select '{0}' and run Calibrate detent... there.", to));
            else if (GetJsonString(cal, "Source") == "copy")
                parts.Add(T("Its axis uses this axis's calibration until you calibrate it."));
            List<string> own = root.TryGetProperty("Groups", out JsonElement groups) && groups.ValueKind == JsonValueKind.Array
                ? groups.EnumerateArray()
                    .SelectMany(g => g.TryGetProperty("Games", out JsonElement games) ? games.EnumerateArray() : Enumerable.Empty<JsonElement>())
                    .Where(g => GetJsonString(g, "OwnScope").Length > 0)
                    .Select(g => GetJsonString(g, "Title"))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList()
                : [];
            if (own.Count > 0)
                parts.Add(T("Its own curves for {0} win over detent mode there.", string.Join(", ", own)));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
        }
        return string.Join(" ", parts);
    }
}
