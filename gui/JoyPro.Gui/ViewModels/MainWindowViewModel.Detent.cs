using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JoyPro.Gui.Models;
using JoyPro.Gui.Localization;
using JoyPro.Gui.Services;
using JoyPro.History;

namespace JoyPro.Gui.ViewModels;

// Detent mode (Binds page, axis relation, "Detent mode"): one throttle relation for all aircraft; per aircraft the
// engine bends the curve so the device's afterburner detent is where that aircraft's afterburner starts
// (docs/axis-curves.md "Detent mode"). This part shows the aircraft groups (binds detent show), switches the mode and
// the no-afterburner rule (binds detent set), runs the calibration wizard on the live device axis (binds detent
// calibrate) and merges per-aircraft throttle relations into one (binds detent merge, undo through the history).
public sealed partial class MainWindowViewModel
{
    public ObservableCollection<DetentGroupItem> DetentGroups { get; } = [];

    public ObservableCollection<DetentMergeProposalItem> DetentMergeProposals { get; } = [];

    [ObservableProperty]
    private bool isDetentExpanded;

    [ObservableProperty]
    private bool detentLoaded;

    [ObservableProperty]
    private bool detentOn;

    // Aircraft without afterburner: true = full power at the detent, false = the relation's curve.
    [ObservableProperty]
    private bool detentNoAbFull = true;

    [ObservableProperty]
    private string detentStatus = T("Select a throttle axis relation.");

    [ObservableProperty]
    private string detentCalibrationText = string.Empty;

    [ObservableProperty]
    private string detentWarningText = string.Empty;

    [ObservableProperty]
    private double detentCalibratedTravel = double.NaN;

    [ObservableProperty]
    private bool detentHasCalibration;

    // ---- wizard ----

    [ObservableProperty]
    private bool isDetentWizardOpen;

    // 0 idle, 1 detent, 2 full, 3 cut-off, 4 summary
    [ObservableProperty]
    private int detentWizardStep;

    [ObservableProperty]
    private double detentWizardRaw = double.NaN;

    [ObservableProperty]
    private string detentWizardLiveText = string.Empty;

    [ObservableProperty]
    private string detentWizardError = string.Empty;

    // ---- merge ----

    [ObservableProperty]
    private bool isDetentMergeOpen;

    [ObservableProperty]
    private string detentMergeStatus = string.Empty;

    [ObservableProperty]
    private bool detentMergeUndoAvailable;

    private string detentRelation = string.Empty;
    private int detentMergeRestoreId;
    private int detentLoadVersion;
    private bool detentApplyingSettings;
    private CancellationTokenSource? detentMonitorCts;
    private string detentMonitorDevice = string.Empty;
    private int detentMonitorAxis = -1;
    private double detentCapturedIdle = double.NaN;
    private double detentCapturedDetent = double.NaN;
    private double detentCapturedFull = double.NaN;
    private double detentCapturedCutoff = double.NaN;

    public const int DetentWizardSummaryStep = 4;

    public bool HasDetentWarning => DetentWarningText.Length > 0;

    public bool HasDetentWizardError => DetentWizardError.Length > 0;

    public bool HasDetentGroups => DetentGroups.Count > 0;

    public bool HasDetentMergeProposals => DetentMergeProposals.Count > 0;

    public bool IsDetentWizardSummary => DetentWizardStep == DetentWizardSummaryStep;

    public bool IsDetentWizardCapturing => IsDetentWizardOpen && DetentWizardStep < DetentWizardSummaryStep;

    public bool CanSkipDetentWizardStep => DetentWizardStep is 2 or 3;

    public bool CanConfirmDetentWizardStep => IsDetentWizardCapturing && !double.IsNaN(DetentWizardRaw);

    public bool CanGoBackDetentWizard => IsDetentWizardOpen && DetentWizardStep > 0;

    public string DetentWizardStepTitle => DetentWizardStep switch
    {
        0 => T("Step 1 of 4: idle"),
        1 => T("Step 2 of 4: the afterburner detent"),
        2 => T("Step 3 of 4: full (optional)"),
        3 => T("Step 4 of 4: cut-off (optional)"),
        _ => T("Done: check and save")
    };

    public string DetentWizardStepText => DetentWizardStep switch
    {
        0 => T("Pull the throttle back to idle and leave it there. If your device has an idle detent (a gate in front of a cut-off), rest the lever on that idle detent, not behind it. Then press Confirm."),
        1 => T("Push the throttle forward to the afterburner detent and stop where you feel the gate; do not push over it. Then press Confirm."),
        2 => T("Push the throttle fully forward, over the detent. Press Confirm, or Skip to use the end of the axis."),
        3 => T("Only if your device has an idle cut-off: lift or push the lever over the idle detent and pull it fully back. Press Confirm, or Skip when your device has none."),
        _ => DetentWizardSummaryText()
    };

    // Raw positions as the device reports them (0..1); the summary in travel (0 = back end, 100 % = front end).
    public IReadOnlyList<(string Label, double Raw)> DetentWizardMarks
    {
        get
        {
            List<(string, double)> marks = [];
            if (!double.IsNaN(detentCapturedCutoff))
                marks.Add(("cut-off", detentCapturedCutoff));
            if (!double.IsNaN(detentCapturedIdle))
                marks.Add(("idle", detentCapturedIdle));
            if (!double.IsNaN(detentCapturedDetent))
                marks.Add(("detent", detentCapturedDetent));
            if (!double.IsNaN(detentCapturedFull))
                marks.Add(("full", detentCapturedFull));
            return marks;
        }
    }

    public string DetentWizardMarksText => string.Join("   ", DetentWizardMarks.Select(m => m.Label + " " + RawPercent(m.Raw)));

    partial void OnIsDetentExpandedChanged(bool value)
    {
        if (value && SelectedBindIsAxis && SelectedBind != null &&
            !string.Equals(detentRelation, SelectedBind.RelationName, StringComparison.OrdinalIgnoreCase))
        {
            _ = LoadDetentAsync();
        }
        if (!value)
            CloseDetentWizard();
    }

    partial void OnDetentWarningTextChanged(string value) => OnPropertyChanged(nameof(HasDetentWarning));

    partial void OnDetentWizardErrorChanged(string value) => OnPropertyChanged(nameof(HasDetentWizardError));

    partial void OnDetentWizardStepChanged(int value) => RaiseDetentWizardState();

    partial void OnIsDetentWizardOpenChanged(bool value) => RaiseDetentWizardState();

    partial void OnDetentWizardRawChanged(double value)
    {
        OnPropertyChanged(nameof(CanConfirmDetentWizardStep));
        DetentWizardLiveText = double.IsNaN(value) ? T("Waiting for the device: move the throttle a little.") : T("Now at {0} of the axis.", RawPercent(value));
    }

    private void RaiseDetentWizardState()
    {
        OnPropertyChanged(nameof(IsDetentWizardSummary));
        OnPropertyChanged(nameof(IsDetentWizardCapturing));
        OnPropertyChanged(nameof(CanSkipDetentWizardStep));
        OnPropertyChanged(nameof(CanConfirmDetentWizardStep));
        OnPropertyChanged(nameof(CanGoBackDetentWizard));
        OnPropertyChanged(nameof(DetentWizardStepTitle));
        OnPropertyChanged(nameof(DetentWizardStepText));
        OnPropertyChanged(nameof(DetentWizardMarks));
        OnPropertyChanged(nameof(DetentWizardMarksText));
    }

    partial void OnDetentOnChanged(bool value)
    {
        if (!detentApplyingSettings && DetentLoaded)
            _ = SetDetentAsync(value ? ["--on"] : ["--off"], value ? T("Detent mode on.") : T("Detent mode off."));
    }

    partial void OnDetentNoAbFullChanged(bool value)
    {
        if (!detentApplyingSettings && DetentLoaded)
            _ = SetDetentAsync(["--no-ab", value ? "full" : "linear"],
                value ? T("Aircraft without afterburner: full power at the detent.") : T("Aircraft without afterburner: the relation's curve."));
    }

    // Called when the bind selection changes.
    private void OnDetentBindSelectionChanged()
    {
        CloseDetentWizard();
        detentLoadVersion++;
        detentRelation = string.Empty;
        DetentLoaded = false;
        DetentGroups.Clear();
        OnPropertyChanged(nameof(HasDetentGroups));
        DetentWarningText = string.Empty;
        DetentCalibrationText = string.Empty;
        DetentHasCalibration = false;
        DetentCalibratedTravel = double.NaN;
        DetentStatus = SelectedBindIsAxis ? T("Loading...") : T("Select a throttle axis relation.");
        if (IsDetentExpanded && SelectedBindIsAxis && SelectedBind != null)
            _ = LoadDetentAsync();
    }

    [RelayCommand]
    private async Task ReloadDetentAsync() => await LoadDetentAsync();

    private async Task LoadDetentAsync()
    {
        BindListItem? bind = SelectedBind;
        if (bind == null || !SelectedBindIsAxis)
            return;
        string relation = bind.RelationName;
        int version = ++detentLoadVersion;
        detentRelation = relation;
        DetentStatus = T("Loading...");
        EngineCommandResult? result = await RunEngineAsync(["binds", "detent", "show", relation], includeProfile: true, mutatesProfile: false,
            busyLabel: T("Loading detent mode..."), forceText: false);
        if (version != detentLoadVersion)
            return;
        if (result == null || result.ExitCode != 0)
        {
            DetentStatus = T("Could not load detent mode (see output).");
            return;
        }
        ApplyDetentShow(result.StandardOutput);
    }

    internal void ApplyDetentShow(string output)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(ExtractJsonPayload(output));
            JsonElement root = doc.RootElement;
            JsonElement settings = root.GetProperty("Settings");
            detentApplyingSettings = true;
            DetentOn = settings.TryGetProperty("On", out JsonElement on) && on.ValueKind == JsonValueKind.True;
            DetentNoAbFull = GetJsonString(settings, "NoAfterburner") != "linear";
            detentApplyingSettings = false;

            string deviceLabel = GetJsonString(root, "DeviceLabel");
            double detentTravel = double.NaN;
            if (root.TryGetProperty("Calibration", out JsonElement cal) && cal.ValueKind == JsonValueKind.Object)
            {
                double idleTravel = cal.GetProperty("IdleTravel").GetDouble();
                detentTravel = cal.GetProperty("DetentTravel").GetDouble();
                string source = GetJsonString(cal, "Source");
                string when = GetJsonString(cal, "When");
                string idleText = DetentGroupItem.Percent(idleTravel);
                string detentText = DetentGroupItem.Percent(detentTravel);
                DetentCalibrationText = source == "profile"
                    ? T("{0}: idle at {1}, afterburner detent at {2} of the travel (read from your old throttle curves; run the wizard to measure it)", deviceLabel, idleText, detentText)
                    : source == "wizard"
                        ? T("{0}: idle at {1}, afterburner detent at {2} of the travel (measured {3})", deviceLabel, idleText, detentText, when)
                        : T("{0}: idle at {1}, afterburner detent at {2} of the travel ({3})", deviceLabel, idleText, detentText, source);
                DetentHasCalibration = true;
            }
            else
            {
                DetentCalibrationText = deviceLabel.Length > 0 ? T("{0}: not calibrated yet. Run the detent wizard so JoyPro knows where your detent is.", deviceLabel) : T("Bind the throttle axis first.");
                DetentHasCalibration = false;
            }
            DetentCalibratedTravel = detentTravel;

            DetentGroups.Clear();
            foreach (JsonElement group in root.GetProperty("Groups").EnumerateArray())
                DetentGroups.Add(ParseDetentGroup(group, detentTravel));
            OnPropertyChanged(nameof(HasDetentGroups));

            List<string> warnings = root.GetProperty("Warnings").EnumerateArray().Select(w => w.GetString() ?? string.Empty).Where(w => w.Length > 0).ToList();
            DetentWarningText = string.Join(" ", warnings);
            string message = GetJsonString(root, "Message");
            int aircraft = DetentGroups.Sum(g => g.Aircraft.Count);
            string groups = Plural("{0} group", "{0} groups", DetentGroups.Count);
            string state = DetentOn ? T("Detent mode is on: {0} aircraft in {1}.", aircraft, groups) : T("Detent mode is off: {0} aircraft in {1}.", aircraft, groups);
            DetentStatus = (message.Length > 0 ? Loc.Translate(message) + " " : string.Empty) + state;
            DetentLoaded = true;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            detentApplyingSettings = false;
            DetentStatus = T("The engine's detent answer could not be read.");
        }
    }

    private static DetentGroupItem ParseDetentGroup(JsonElement group, double detentTravel)
    {
        List<(string, string, string, string, string, double?)> aircraft = [];
        foreach (JsonElement a in group.GetProperty("Aircraft").EnumerateArray())
        {
            double? data = a.TryGetProperty("GameData", out JsonElement d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : null;
            aircraft.Add((GetJsonString(a, "Game"), GetJsonString(a, "Plane"), GetJsonString(a, "Title"), GetJsonString(a, "Source"), GetJsonString(a, "Confidence"), data));
        }
        List<string> lines = [];
        List<string> warnings = [];
        List<Point> throttle = [];
        foreach (JsonElement game in group.GetProperty("Games").EnumerateArray())
        {
            string title = GetJsonString(game, "Title");
            string own = GetJsonString(game, "OwnScope");
            if (own.Length > 0)
            {
                lines.Add(T("{0}: own tuning ({1}) wins over detent mode", title, own));
                continue;
            }
            if (!game.TryGetProperty("Fit", out JsonElement fit) || fit.ValueKind != JsonValueKind.Object)
            {
                lines.Add(GetJsonString(group, "Kind") == "Linear" ? T("{0}: the relation's curve", title) : T("{0}: waits for the calibration", title));
                continue;
            }
            bool fallback = fit.TryGetProperty("Fallback", out JsonElement fb) && fb.ValueKind == JsonValueKind.True;
            double error = fit.TryGetProperty("MaxError", out JsonElement err) ? err.GetDouble() : 0;
            string method = Loc.Translate(GetJsonString(fit, "Method"));
            lines.Add(fallback ? T("{0}: keeps its normal curve", title)
                : error < 0.002 ? T("{0}: {1}, exact", title, method)
                : T("{0}: {1}, off by up to {2}", title, method, DetentGroupItem.Percent(error)));
            string warning = GetJsonString(fit, "Warning");
            if (warning.Length > 0)
                warnings.Add(Loc.Translate(warning));
            if (throttle.Count == 0 && fit.TryGetProperty("Throttle", out JsonElement points))
            {
                foreach (JsonElement p in points.EnumerateArray())
                    throttle.Add(new Point(p[0].GetDouble(), p[1].GetDouble()));
            }
        }
        double? ab = group.TryGetProperty("AbStart", out JsonElement abValue) && abValue.ValueKind == JsonValueKind.Number ? abValue.GetDouble() : null;
        return new DetentGroupItem(GetJsonString(group, "Target"), GetJsonString(group, "Kind"), ab, GetJsonString(group, "Describe"), GetJsonString(group, "Label"),
            aircraft, lines, warnings, throttle, detentTravel);
    }

    private async Task SetDetentAsync(List<string> options, string done)
    {
        if (detentRelation.Length == 0)
            return;
        List<string> args = ["binds", "detent", "set", detentRelation];
        args.AddRange(options);
        EngineCommandResult? result = await RunEngineAsync(args, includeProfile: true, mutatesProfile: true, busyLabel: T("Saving detent mode..."), forceText: false);
        if (result?.ExitCode != 0)
        {
            await LoadDetentAsync();
            return;
        }
        ApplyDetentShow(result.StandardOutput);
        StatusText = done;
        if (IsAxisTuningExpanded)
            await LoadAxisTuningScopeAsync();
    }

    // A group's afterburner start typed in % (or empty: back to the game data) for every aircraft of the group.
    [RelayCommand]
    private async Task SetDetentGroupAbAsync(DetentGroupItem? group)
    {
        if (group == null || group.Aircraft.Count == 0)
            return;
        string text = (group.AbText ?? string.Empty).Trim().TrimEnd('%').Trim().Replace(',', '.');
        List<string> options = [];
        string value;
        if (text.Length == 0)
            value = "default";
        else if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double percent) && percent > 0 && percent <= 100)
            value = "ab:" + (percent / 100.0).ToString("0.####", CultureInfo.InvariantCulture);
        else
        {
            StatusText = T("Type the afterburner start in % of the game's throttle (1..100), or leave it empty for the game data.");
            return;
        }
        foreach ((string game, string plane, _, _, _, _) in group.Aircraft)
            options.AddRange(["--target", game + ":" + plane + "=" + value]);
        await SetDetentAsync(options, value == "default" ? T("{0}: back to the game data.", group.Label) : T("{0}: afterburner at {1} %.", group.Label, text));
    }

    // A group back to what the game data says (its own values removed).
    [RelayCommand]
    private async Task ResetDetentGroupAsync(DetentGroupItem? group)
    {
        if (group == null || group.Aircraft.Count == 0)
            return;
        List<string> options = [];
        foreach ((string game, string plane, _, _, _, _) in group.Aircraft)
            options.AddRange(["--target", game + ":" + plane + "=default"]);
        await SetDetentAsync(options, T("{0}: back to the game data.", group.Label));
    }

    // ---- calibration wizard ------------------------------------------------------------------------------------------

    [RelayCommand]
    private void StartDetentWizard()
    {
        BindListItem? bind = SelectedBind;
        int axis = bind == null ? -1 : HudDevice.AxisIndex((bind.Input ?? string.Empty).Trim());
        if (bind == null || !bind.HasBind || axis < 0 || string.IsNullOrWhiteSpace(bind.Device))
        {
            StatusText = T("Bind the throttle to a device axis first.");
            return;
        }
        detentCapturedIdle = double.NaN;
        detentCapturedDetent = double.NaN;
        detentCapturedFull = double.NaN;
        detentCapturedCutoff = double.NaN;
        DetentWizardError = string.Empty;
        DetentWizardRaw = double.NaN;
        DetentWizardStep = 0;
        IsDetentWizardOpen = true;
        RaiseDetentWizardState();
        StartDetentMonitor(bind.Device.Trim(), axis);
    }

    private void StartDetentMonitor(string device, int axis)
    {
        StopDetentMonitor();
        detentMonitorDevice = device;
        detentMonitorAxis = axis;
        detentMonitorCts = new CancellationTokenSource();
        _ = MonitorDetentAsync(device, detentMonitorCts.Token);
    }

    private async Task MonitorDetentAsync(string device, CancellationToken token)
    {
        try
        {
            await joystickCaptureService.MonitorAsync(
                _ => { },
                snapshot =>
                {
                    if (!token.IsCancellationRequested && OverlayResolver.SameDevice(device, snapshot.Device))
                        Dispatcher.UIThread.Post(() => OnDetentAxes(snapshot));
                },
                token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            DetentWizardLiveText = T("Live position unavailable: {0}", ex.Message);
        }
    }

    private void StopDetentMonitor()
    {
        detentMonitorCts?.Cancel();
        detentMonitorCts?.Dispose();
        detentMonitorCts = null;
    }

    internal void OnDetentAxes(JoystickAxisSnapshot snapshot)
    {
        if (!IsDetentWizardOpen || detentMonitorAxis < 0 || detentMonitorAxis >= snapshot.Axes.Length ||
            !OverlayResolver.SameDevice(detentMonitorDevice, snapshot.Device))
        {
            return;
        }
        DetentWizardRaw = Math.Clamp(snapshot.Axes[detentMonitorAxis], 0.0, 1.0);
    }

    [RelayCommand]
    private void ConfirmDetentWizardStep()
    {
        if (!IsDetentWizardCapturing || double.IsNaN(DetentWizardRaw))
            return;
        double raw = Math.Round(DetentWizardRaw, 4);
        DetentWizardError = string.Empty;
        switch (DetentWizardStep)
        {
            case 0:
                detentCapturedIdle = raw;
                break;
            case 1:
                if (Math.Abs(raw - detentCapturedIdle) < 0.02)
                {
                    DetentWizardError = T("The throttle is still at idle: push it forward to the afterburner detent.");
                    return;
                }
                detentCapturedDetent = raw;
                break;
            case 2:
                if (Increasing() ? raw <= detentCapturedDetent + 0.005 : raw >= detentCapturedDetent - 0.005)
                {
                    DetentWizardError = T("That is not beyond the detent: push the throttle fully forward, or Skip.");
                    return;
                }
                detentCapturedFull = raw;
                break;
            case 3:
                if (Math.Abs(raw - detentCapturedIdle) < 0.01)
                {
                    DetentWizardError = T("That is idle itself: pull the lever over the idle detent, or Skip when your device has no cut-off.");
                    return;
                }
                if (Increasing() ? raw > detentCapturedIdle : raw < detentCapturedIdle)
                {
                    DetentWizardError = T("The cut-off is behind idle: pull the lever back, not forward.");
                    return;
                }
                detentCapturedCutoff = raw;
                break;
        }
        DetentWizardStep++;
        RaiseDetentWizardState();
    }

    [RelayCommand]
    private void SkipDetentWizardStep()
    {
        if (!CanSkipDetentWizardStep)
            return;
        if (DetentWizardStep == 2)
            detentCapturedFull = double.NaN;
        else
            detentCapturedCutoff = double.NaN;
        DetentWizardError = string.Empty;
        DetentWizardStep++;
        RaiseDetentWizardState();
    }

    [RelayCommand]
    private void BackDetentWizardStep()
    {
        if (DetentWizardStep <= 0)
            return;
        DetentWizardError = string.Empty;
        DetentWizardStep--;
        RaiseDetentWizardState();
    }

    [RelayCommand]
    private void CancelDetentWizard() => CloseDetentWizard();

    private void CloseDetentWizard()
    {
        StopDetentMonitor();
        if (IsDetentWizardOpen)
            IsDetentWizardOpen = false;
    }

    [RelayCommand]
    private async Task SaveDetentWizardAsync()
    {
        if (!IsDetentWizardSummary || double.IsNaN(detentCapturedIdle) || double.IsNaN(detentCapturedDetent) || detentRelation.Length == 0)
            return;
        List<string> args = BuildDetentCalibrateArguments();
        EngineCommandResult? result = await RunEngineAsync(args, includeProfile: true, mutatesProfile: true, busyLabel: T("Saving the detent calibration..."), forceText: false);
        if (result?.ExitCode != 0)
        {
            DetentWizardError = T("The engine did not take the calibration (see output).");
            return;
        }
        CloseDetentWizard();
        ApplyDetentShow(result.StandardOutput);
        StatusText = T("Detent calibration saved.");
        if (IsAxisTuningExpanded)
            await LoadAxisTuningScopeAsync();
    }

    internal List<string> BuildDetentCalibrateArguments()
    {
        List<string> args = ["binds", "detent", "calibrate", detentRelation, "--idle", Num(detentCapturedIdle), "--detent", Num(detentCapturedDetent), "--source", "wizard"];
        args.AddRange(["--full", double.IsNaN(detentCapturedFull) ? "none" : Num(detentCapturedFull)]);
        args.AddRange(["--cutoff", double.IsNaN(detentCapturedCutoff) ? "none" : Num(detentCapturedCutoff)]);
        return args;

        static string Num(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);
    }

    private bool Increasing() => double.IsNaN(detentCapturedDetent) || double.IsNaN(detentCapturedIdle) || detentCapturedDetent >= detentCapturedIdle;

    // Travel 0..1 from the back end of a raw position, once the direction is known.
    private double TravelOf(double raw) => Increasing() ? raw : 1.0 - raw;

    private string DetentWizardSummaryText()
    {
        if (double.IsNaN(detentCapturedIdle) || double.IsNaN(detentCapturedDetent))
            return string.Empty;
        List<string> parts = [T("Idle at {0}, afterburner detent at {1} of the travel", DetentGroupItem.Percent(TravelOf(detentCapturedIdle)), DetentGroupItem.Percent(TravelOf(detentCapturedDetent)))];
        if (!double.IsNaN(detentCapturedFull))
            parts.Add(T("full at {0}", DetentGroupItem.Percent(TravelOf(detentCapturedFull))));
        if (!double.IsNaN(detentCapturedCutoff))
            parts.Add(T("cut-off at {0}", DetentGroupItem.Percent(TravelOf(detentCapturedCutoff))));
        return string.Join(", ", parts) + ". " + (Increasing() ? T("The axis value rises towards full.") : T("The axis value falls towards full (reversed axis).")) + " " +
               T("Save stores this for {0} {1}; every throttle relation on this axis uses it.", SelectedBind?.Device ?? string.Empty, SelectedBind?.Input ?? string.Empty);
    }

    private static string RawPercent(double raw) => DetentGroupItem.Percent(raw);

    // ---- merge helper -----------------------------------------------------------------------------------------------

    [RelayCommand]
    private async Task OpenDetentMergeAsync()
    {
        IsDetentMergeOpen = true;
        DetentMergeStatus = T("Looking for throttle relations to merge...");
        EngineCommandResult? result = await RunEngineAsync(["binds", "detent", "merge"], includeProfile: true, mutatesProfile: false,
            busyLabel: T("Planning the throttle merge..."), forceText: false);
        if (result == null || result.ExitCode != 0)
        {
            DetentMergeStatus = T("Could not plan the merge (see output).");
            return;
        }
        ApplyDetentMergePlan(result.StandardOutput);
    }

    internal void ApplyDetentMergePlan(string output)
    {
        DetentMergeProposals.Clear();
        try
        {
            using JsonDocument doc = JsonDocument.Parse(ExtractJsonPayload(output));
            foreach (JsonElement p in doc.RootElement.GetProperty("Proposals").EnumerateArray())
            {
                List<string> relations = p.GetProperty("Sources").EnumerateArray().Select(s => GetJsonString(s, "Relation")).ToList();
                string calibration = string.Empty;
                if (p.TryGetProperty("Calibration", out JsonElement cal) && cal.ValueKind == JsonValueKind.Object)
                {
                    bool inferred = cal.TryGetProperty("Inferred", out JsonElement inf) && inf.ValueKind == JsonValueKind.True;
                    string at = DetentGroupItem.Percent(cal.GetProperty("DetentTravel").GetDouble());
                    calibration = inferred ? T("Device detent at {0} of the travel ({1})", at, Loc.Translate(GetJsonString(cal, "Note"))) : T("Device detent at {0} of the travel (calibrated)", at);
                }
                List<string> lines = p.GetProperty("Targets").EnumerateArray()
                    .GroupBy(t => GetJsonString(t, "Describe"))
                    .Select(g => T("{0}: {1} (from {2})", string.Join(", ", g.Select(t => GetJsonString(t, "Title")).Distinct()), Loc.Translate(g.Key),
                        string.Join(" / ", g.Select(t => Loc.Translate(GetJsonString(t, "From"))).Distinct())))
                    .ToList();
                List<string> notes = p.GetProperty("Notes").EnumerateArray().Select(n => n.GetString() ?? string.Empty).ToList();
                List<string> problems = p.GetProperty("Problems").EnumerateArray().Select(n => n.GetString() ?? string.Empty).ToList();
                DetentMergeProposals.Add(new DetentMergeProposalItem(GetJsonString(p, "DeviceLabel"), GetJsonString(p, "Into"), relations, calibration, lines, notes, problems));
            }
            DetentMergeStatus = DetentMergeProposals.Count == 0
                ? T("No device axis has two or more throttle relations: nothing to merge.")
                : T("Merging keeps the first relation's bind and curve, adds the others' aircraft to it and removes the others. Undo takes it back.");
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            DetentMergeStatus = T("The engine's merge plan could not be read.");
        }
        OnPropertyChanged(nameof(HasDetentMergeProposals));
    }

    [RelayCommand]
    private async Task ApplyDetentMergeAsync(DetentMergeProposalItem? proposal)
    {
        if (proposal == null || !proposal.CanApply)
            return;
        List<string> args = ["binds", "detent", "merge", "--relations", string.Join(",", proposal.Relations), "--into", proposal.Into, "--apply"];
        EngineCommandResult? result = await RunEngineAsync(args, includeProfile: true, mutatesProfile: true, busyLabel: T("Merging throttle relations..."), forceText: false,
            historyNote: "Detent mode: merged " + string.Join(", ", proposal.Relations) + " into " + proposal.Into);
        if (result?.ExitCode != 0)
        {
            DetentMergeStatus = T("The merge failed (see output).");
            return;
        }
        DetentMergeProposals.Remove(proposal);
        OnPropertyChanged(nameof(HasDetentMergeProposals));
        // Undo merge writes back the version the merge was made on (also when other changes came after it).
        detentMergeRestoreId = 0;
        try
        {
            if (ProfileHistory.Enabled && HasProfile)
            {
                ProfileHistoryLog log = ProfileHistory.Read(ProfilePath.Trim());
                ProfileVersion? merge = log.Logical(log.CursorVersion);
                detentMergeRestoreId = merge?.Parent ?? 0;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            detentMergeRestoreId = 0;
        }
        DetentMergeUndoAvailable = true;
        DetentMergeStatus = T("Merged {0} into {1}. Undo merge takes it back.", string.Join(", ", proposal.Relations), proposal.Into);
        await RefreshBindsPageAsync();
        BindListItem? merged = Binds.FirstOrDefault(b => string.Equals(b.RelationName, proposal.Into, StringComparison.OrdinalIgnoreCase));
        if (merged != null)
            SelectedBind = merged;
        IsDetentExpanded = true;
        await LoadDetentAsync();
        StatusText = DetentMergeStatus;
    }

    [RelayCommand]
    private async Task UndoDetentMergeAsync()
    {
        if (!DetentMergeUndoAvailable)
            return;
        int restore = detentMergeRestoreId;
        if (restore > 0)
            await WriteHistoryAsync(path => ProfileHistory.Restore(path, restore), T("Undo merge"));
        else
            await UndoLastChangeAsync();
        DetentMergeUndoAvailable = false;
        detentMergeRestoreId = 0;
        DetentMergeStatus = T("Merge undone: the throttle relations are back as before the merge (Redo or the history brings the merge back).");
        await RefreshBindsPageAsync();
        await LoadDetentAsync();
    }

    [RelayCommand]
    private void CloseDetentMerge()
    {
        IsDetentMergeOpen = false;
    }
}
