using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using JoyPro.Gui.Services;

namespace JoyPro.Gui.ViewModels;

// The busy area of a running engine command (Start page and bottom bar): what it does now, how far it is, how long it
// runs and about how long it still needs, the last status lines, and Cancel where the engine allows it. Fed with the
// engine's stderr lines (EngineProgressParser) on the UI thread; Tick() updates the clock between lines.
public sealed partial class BusyProgressViewModel : ViewModelBase
{
    public const int MaxDetailLines = 50;

    // Remaining time is estimated only after this many finished items and this much time in the phase.
    private const int MinSamplesForEstimate = 3;
    private static readonly TimeSpan MinTimeForEstimate = TimeSpan.FromSeconds(2);

    private DateTime startedAt;
    private string phase = string.Empty;
    private int? phaseTotal;
    private DateTime phaseStartedAt;
    private int phaseFirstCurrent;
    private int lastCurrent;
    private DateTime lastChangeAt;
    private bool cancelOffered;

    // A command runs (the area is shown).
    [ObservableProperty]
    private bool isActive;

    // The busy label of the command ("Refreshing the DCS World DB...").
    [ObservableProperty]
    private string title = string.Empty;

    // Part of a batch ("Command list 2 of 5: MSFS 2024"); empty otherwise.
    [ObservableProperty]
    private string batchText = string.Empty;

    // The game or area the engine reports ("DCS World").
    [ObservableProperty]
    private string gameText = string.Empty;

    // What it works on now ("F-16C_50: reading inputs").
    [ObservableProperty]
    private string stepText = string.Empty;

    // "23 / 59" (or "23" when the engine does not know the total).
    [ObservableProperty]
    private string countText = string.Empty;

    // Progress lines with a total came: a determinate bar.
    [ObservableProperty]
    private bool isDeterminate;

    [ObservableProperty]
    private double percent;

    [ObservableProperty]
    private string elapsedText = "0:00";

    // "about 2 min left" once there are enough samples; empty before.
    [ObservableProperty]
    private string remainingText = string.Empty;

    // "1:23 elapsed - about 2 min left".
    [ObservableProperty]
    private string timeText = string.Empty;

    [ObservableProperty]
    private bool detailsExpanded;

    // The engine offers a cancel now ([engine-cancel] available) and the user did not press it yet.
    [ObservableProperty]
    private bool canCancel;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCancel))]
    private bool isCancelling;

    // The Cancel button (MainWindowViewModel.CancelEngineRunCommand).
    [ObservableProperty]
    private System.Windows.Input.ICommand? cancelCommand;

    // The button is there while a cancel is offered or asked for ("Cancelling..." then).
    public bool ShowCancel => CanCancel || IsCancelling;

    public string CancelLabel => IsCancelling ? T("Cancelling...") : T("Cancel");

    partial void OnCanCancelChanged(bool value) => OnPropertyChanged(nameof(ShowCancel));

    partial void OnIsCancellingChanged(bool value) => OnPropertyChanged(nameof(CancelLabel));

    // The command runs for a second or reports progress: worth showing outside the Start page (no flicker for the many
    // short calls).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCompact))]
    [NotifyPropertyChangedFor(nameof(ShowPanel))]
    private bool isLong;

    // The Start page's large busy area.
    public bool ShowPanel => IsActive && IsLong;

    // The Start page is open; it has the large busy area, the bottom bar shows none then.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCompact))]
    private bool onStartPage;

    // The compact busy row above the bottom bar's status line.
    public bool ShowCompact => IsActive && IsLong && !OnStartPage;

    partial void OnIsActiveChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowCompact));
        OnPropertyChanged(nameof(ShowPanel));
    }

    // The last status, progress and warning lines of the command, newest first (at most MaxDetailLines).
    public ObservableCollection<string> DetailLines { get; } = [];

    public bool HasDetails => DetailLines.Count > 0;

    public BusyProgressViewModel()
    {
        DetailLines.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasDetails));
    }

    public void Begin(string commandTitle, DateTime now, string batch = "")
    {
        startedAt = now;
        phase = string.Empty;
        phaseTotal = null;
        phaseStartedAt = now;
        phaseFirstCurrent = 0;
        lastCurrent = 0;
        lastChangeAt = now;
        cancelOffered = false;
        Title = commandTitle ?? string.Empty;
        BatchText = batch ?? string.Empty;
        GameText = string.Empty;
        StepText = string.Empty;
        CountText = string.Empty;
        IsDeterminate = false;
        Percent = 0;
        RemainingText = string.Empty;
        CanCancel = false;
        IsCancelling = false;
        IsLong = false;
        DetailLines.Clear();
        IsActive = true;
        Tick(now);
    }

    public void End()
    {
        IsActive = false;
        CanCancel = false;
    }

    // One stderr line of the engine.
    public void Apply(string line, DateTime now)
    {
        Apply([line], now);
    }

    // A batch of stderr lines (the UI thread takes them in batches; an import reports thousands of files).
    public void Apply(IReadOnlyList<string> lines, DateTime now)
    {
        if (lines == null || lines.Count == 0)
            return;
        List<string> details = [];
        foreach (string raw in lines)
        {
            EngineStderrLine line = EngineProgressParser.Parse(raw);
            switch (line.Kind)
            {
                case EngineStderrKind.Progress:
                    ApplyProgress(line, now);
                    details.Add(Stamp(now) + line.Current.ToString(CultureInfo.InvariantCulture) + "/" +
                                (line.Total?.ToString(CultureInfo.InvariantCulture) ?? "?") + "  " + line.Phase +
                                (line.Item.Length > 0 ? ": " + line.Item : string.Empty));
                    break;
                case EngineStderrKind.Status:
                    if (line.Text.Length == 0)
                        break;
                    // Without progress lines the latest status message is the step.
                    if (!IsDeterminate && string.IsNullOrEmpty(CountText))
                        StepText = Translate(line.Text);
                    details.Add(Stamp(now) + line.Text);
                    break;
                case EngineStderrKind.Cancel:
                    if (line.CancelState == "available")
                        cancelOffered = true;
                    else if (line.CancelState == "unavailable" || line.CancelState == "cancelled")
                        cancelOffered = false;
                    if (line.CancelState == "unavailable")
                        details.Add(T("{0}Writing files now: this step cannot be cancelled.", Stamp(now)));
                    break;
                default:
                    if (line.Text.Trim().Length > 0)
                        details.Add(Stamp(now) + line.Text.Trim());
                    break;
            }
        }
        CanCancel = cancelOffered && !IsCancelling;
        AddDetails(details);
        Tick(now);
    }

    // The user pressed Cancel: the engine stops at its next safe point.
    public void MarkCancelling(DateTime now)
    {
        IsCancelling = true;
        CanCancel = false;
        AddDetails([Stamp(now) + "Cancel requested: JoyPro stops before it writes anything."]);
    }

    // Updates the clock and the estimate (called every second while the command runs, and after each batch of lines).
    public void Tick(DateTime now)
    {
        TimeSpan elapsed = now - startedAt;
        if (elapsed >= TimeSpan.FromSeconds(1) || !string.IsNullOrEmpty(CountText))
            IsLong = true;
        ElapsedText = FormatClock(elapsed);
        RemainingText = EstimateRemaining(now);
        TimeText = ElapsedText + " elapsed" + (RemainingText.Length > 0 ? " - " + RemainingText : string.Empty);
    }

    private void ApplyProgress(EngineStderrLine line, DateTime now)
    {
        if (!string.Equals(line.Phase, phase, StringComparison.Ordinal) || line.Total != phaseTotal || line.Current < lastCurrent)
        {
            phase = line.Phase;
            phaseTotal = line.Total;
            phaseStartedAt = now;
            phaseFirstCurrent = line.Current;
            lastCurrent = line.Current;
            lastChangeAt = now;
        }
        else if (line.Current != lastCurrent)
        {
            lastCurrent = line.Current;
            lastChangeAt = now;
        }

        // "DCS World, reading inputs" -> game "DCS World", what "reading inputs".
        int comma = line.Phase.IndexOf(", ", StringComparison.Ordinal);
        GameText = comma > 0 ? line.Phase.Substring(0, comma) : string.Empty;
        string what = comma > 0 ? line.Phase.Substring(comma + 2) : line.Phase;
        StepText = line.Item.Length > 0 ? line.Item + ": " + Translate(what) : Translate(Capitalize(what));
        if (line.Total is int total && total > 0)
        {
            IsDeterminate = true;
            Percent = Math.Clamp(100.0 * line.Current / total, 0, 100);
            CountText = line.Current.ToString(CultureInfo.InvariantCulture) + " / " + total.ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            IsDeterminate = false;
            Percent = 0;
            CountText = line.Current > 0 ? line.Current.ToString(CultureInfo.InvariantCulture) : string.Empty;
        }
    }

    private string EstimateRemaining(DateTime now)
    {
        if (phaseTotal is not int total || total <= 0)
            return string.Empty;
        int finished = lastCurrent - phaseFirstCurrent;
        TimeSpan measured = lastChangeAt - phaseStartedAt;
        if (finished < MinSamplesForEstimate || measured < MinTimeForEstimate)
            return string.Empty;
        double perItem = measured.TotalSeconds / finished;
        // The item in work (lastCurrent) is not finished yet: it and the ones after it remain.
        double remaining = perItem * (total - lastCurrent + 1) - (now - lastChangeAt).TotalSeconds;
        return FormatRemaining(Math.Max(0, remaining));
    }

    internal static string FormatRemaining(double seconds)
    {
        if (seconds < 5)
            return "almost done";
        if (seconds < 60)
            return "about " + ((int)(Math.Ceiling(seconds / 5) * 5)).ToString(CultureInfo.InvariantCulture) + " s left";
        int minutes = (int)Math.Ceiling(seconds / 60);
        if (minutes < 60)
            return "about " + minutes.ToString(CultureInfo.InvariantCulture) + " min left";
        return "about " + (minutes / 60).ToString(CultureInfo.InvariantCulture) + " h " + (minutes % 60).ToString(CultureInfo.InvariantCulture) + " min left";
    }

    internal static string FormatClock(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
            span = TimeSpan.Zero;
        return span.TotalHours >= 1
            ? ((int)span.TotalHours).ToString(CultureInfo.InvariantCulture) + ":" + span.Minutes.ToString("00", CultureInfo.InvariantCulture) + ":" + span.Seconds.ToString("00", CultureInfo.InvariantCulture)
            : span.Minutes.ToString(CultureInfo.InvariantCulture) + ":" + span.Seconds.ToString("00", CultureInfo.InvariantCulture);
    }

    private string Stamp(DateTime now) => FormatClock(now - startedAt) + "  ";

    private void AddDetails(List<string> details)
    {
        if (details.Count == 0)
            return;
        // Newest first: the list shows what happens now without scrolling.
        int skip = Math.Max(0, details.Count - MaxDetailLines);
        for (int i = skip; i < details.Count; i++)
            DetailLines.Insert(0, details[i]);
        while (DetailLines.Count > MaxDetailLines)
            DetailLines.RemoveAt(DetailLines.Count - 1);
    }

    private static string Capitalize(string text)
    {
        return string.IsNullOrEmpty(text) ? text : char.ToUpperInvariant(text[0]) + text.Substring(1);
    }
}
