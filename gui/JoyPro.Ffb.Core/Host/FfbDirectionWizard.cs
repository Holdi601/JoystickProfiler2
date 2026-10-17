using System.Globalization;
using JoyPro.Ffb.Core.Output;

namespace JoyPro.Ffb.Core.Host;

/// <summary>What the direction test sends to the stick (JoyPro.Ffb test --protocol in the app, a fake in tests).</summary>
/// <remarks>Forces are in JoyPro's convention (positive = right on X, towards the pilot on Y) and pass through the
/// device inverts set with <see cref="SetInvertAsync"/>. Implementations clamp every force to 25 % and stop on their
/// own after a few seconds without a command.</remarks>
public interface IFfbTestOutput
{
    Task SetInvertAsync(bool invertX, bool invertY, CancellationToken token);

    /// <summary>A constant force on one axis until the next command (or the output's own time limit).</summary>
    Task PushAsync(FfbAxis axis, double magnitude, CancellationToken token);

    /// <summary>A centring spring on both axes with its centre at (x, y), -1..1.</summary>
    Task SpringAsync(double stiffness, double centreX, double centreY, CancellationToken token);

    /// <summary>The stick position (x, y, -1..1, right and towards the pilot positive), null when it cannot be read.</summary>
    Task<(double X, double Y)?> ReadPositionAsync(CancellationToken token);

    /// <summary>Stops every force and releases the device. Never throws.</summary>
    Task StopAsync();
}

public enum FfbWizardStep
{
    Intro,
    ReadyRight,
    PushingRight,
    AskRight,
    ReadyForward,
    PushingForward,
    AskForward,
    ReadySpring,
    SpringRunning,
    AskSpring,
    ReadyTrim,
    TrimRunning,
    AskTrim,
    Done,
    Cancelled,
    Failed
}

public enum FfbWizardAnswer
{
    Right,
    Left,
    Forward,
    Back,
    Nothing,
    Yes,
    No
}

/// <summary>A button the wizard offers now: the answer it gives, its label and an icon name (see <see cref="FfbWizardIcons"/>).</summary>
public sealed record FfbWizardChoice(FfbWizardAnswer Answer, string Label, string Icon);

/// <summary>Icon names the GUI maps to symbols.</summary>
public static class FfbWizardIcons
{
    public const string Info = "info";
    public const string Ready = "ready";
    public const string Right = "right";
    public const string Left = "left";
    public const string Forward = "forward";
    public const string Back = "back";
    public const string Nothing = "nothing";
    public const string Spring = "spring";
    public const string Trim = "trim";
    public const string Yes = "yes";
    public const string No = "no";
    public const string Done = "done";
    public const string Warning = "warning";
}

/// <summary>The per-device direction test: pushes right, then forward, asks the user which way the stick went, and
/// finds invertX / invertY; then checks the centring spring and a trimmed centre.</summary>
/// <remarks>
/// Every push is announced and waits for <see cref="ReadyAsync"/> (the user's "Ready" click). Pushes are 20 % for
/// <see cref="PushSeconds"/>; springs are capped at 25 %. The pushes run with both inverts off (the stick's raw
/// direction), so the answers give the inverts directly: right push felt left = invert X; forward push felt back =
/// invert Y. "Nothing" leaves that invert as it was and says why it may be. The spring and trim checks then run with
/// the found inverts. <see cref="CancelAsync"/> works in every step; every path ends with <see cref="IFfbTestOutput.StopAsync"/>.
/// The class is a plain state machine (no UI): the GUI shows <see cref="Title"/>, <see cref="Text"/>,
/// <see cref="Icon"/>, <see cref="Choices"/> and calls back.
/// </remarks>
public sealed class FfbDirectionWizard
{
    public const double PushMagnitude = 0.2;
    public const double MaxTestForce = 0.25;
    public const double SpringStiffness = 0.5;
    public const double TrimCentre = 0.35;

    /// <summary>A trimmed centre counts as "moved" when the stick moved at least this far (fraction of the travel).</summary>
    public const double MovedThreshold = 0.08;

    public const string VpforceHint =
        "VPforce Rhino / TMS: the VPforce Configurator's own spring (Effects tab, box at the top left, \"Spring\") overrides " +
        "the spring JoyPro sends, so trim cannot move the stick. For trim and centring from JoyPro, untick that \"Spring\" " +
        "and press \"Apply Settings\" there; JoyPro then centres the stick itself while it runs (\"JoyPro provides the centring " +
        "spring\" on this page). Keep it ticked if you prefer the stick's own spring (no trim from JoyPro).";

    public const string GenericSpringHint =
        "The stick's own centring (set in its driver or configuration tool) may override the spring JoyPro sends. Switch the " +
        "device's own spring off there for trim from JoyPro, or keep it and switch \"JoyPro provides the centring spring\" off.";

    private readonly IFfbTestOutput _output;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly bool _initialInvertX;
    private readonly bool _initialInvertY;
    private CancellationTokenSource _cts = new();
    private (double X, double Y)? _trimStart;

    public FfbDirectionWizard(IFfbTestOutput output, bool currentInvertX, bool currentInvertY, bool isVpforce,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _output = output;
        _initialInvertX = currentInvertX;
        _initialInvertY = currentInvertY;
        IsVpforce = isVpforce;
        _delay = delay ?? ((time, token) => Task.Delay(time, token));
        Show(FfbWizardStep.Intro);
    }

    public double PushSeconds { get; init; } = 2;
    public double SpringSeconds { get; init; } = 2.5;

    public bool IsVpforce { get; }
    public FfbWizardStep Step { get; private set; }
    public string Title { get; private set; } = "";
    public string Text { get; private set; } = "";
    public string Icon { get; private set; } = FfbWizardIcons.Info;
    public IReadOnlyList<FfbWizardChoice> Choices { get; private set; } = [];

    /// <summary>A force runs now (the GUI shows a progress hint and only Cancel).</summary>
    public bool Busy => Step is FfbWizardStep.PushingRight or FfbWizardStep.PushingForward or FfbWizardStep.SpringRunning or FfbWizardStep.TrimRunning;

    /// <summary>The "Ready" button is offered (an announced step waits for the user).</summary>
    public bool WaitingForReady => Step is FfbWizardStep.Intro or FfbWizardStep.ReadyRight or FfbWizardStep.ReadyForward or FfbWizardStep.ReadySpring or FfbWizardStep.ReadyTrim;

    public bool Finished => Step is FfbWizardStep.Done or FfbWizardStep.Cancelled or FfbWizardStep.Failed;

    /// <summary>The found inverts; null = no answer for that axis (keep the current one).</summary>
    public bool? FoundInvertX { get; private set; }
    public bool? FoundInvertY { get; private set; }

    /// <summary>The inverts to save: the found ones, else the ones the device had.</summary>
    public bool InvertX => FoundInvertX ?? _initialInvertX;
    public bool InvertY => FoundInvertY ?? _initialInvertY;

    public bool? SpringWorks { get; private set; }
    public bool? TrimWorks { get; private set; }

    /// <summary>How far the stick moved during the trim check (null: the position could not be read).</summary>
    public double? TrimMeasured { get; private set; }

    /// <summary>Plain-language notes collected on the way (shown at the end).</summary>
    public List<string> Notes { get; } = [];

    public event Action? Changed;

    /// <summary>The user clicked "Ready": run the announced step (a push or a spring), then ask.</summary>
    public async Task ReadyAsync()
    {
        if (!WaitingForReady)
            return;
        var token = _cts.Token;
        try
        {
            switch (Step)
            {
                case FfbWizardStep.Intro:
                    await _output.SetInvertAsync(false, false, token).ConfigureAwait(false);
                    Show(FfbWizardStep.ReadyRight);
                    return;
                case FfbWizardStep.ReadyRight:
                    Show(FfbWizardStep.PushingRight);
                    await RunPushAsync(FfbAxis.X, PushMagnitude, token).ConfigureAwait(false);
                    Show(FfbWizardStep.AskRight);
                    return;
                case FfbWizardStep.ReadyForward:
                    Show(FfbWizardStep.PushingForward);
                    // Forward = away from the pilot = negative Y in JoyPro's convention.
                    await RunPushAsync(FfbAxis.Y, -PushMagnitude, token).ConfigureAwait(false);
                    Show(FfbWizardStep.AskForward);
                    return;
                case FfbWizardStep.ReadySpring:
                    Show(FfbWizardStep.SpringRunning);
                    await _output.SetInvertAsync(InvertX, InvertY, token).ConfigureAwait(false);
                    await _output.SpringAsync(SpringStiffness, 0, 0, token).ConfigureAwait(false);
                    Show(FfbWizardStep.AskSpring);
                    return;
                case FfbWizardStep.ReadyTrim:
                    Show(FfbWizardStep.TrimRunning);
                    _trimStart = await _output.ReadPositionAsync(token).ConfigureAwait(false);
                    await _output.SpringAsync(SpringStiffness, 0, -TrimCentre, token).ConfigureAwait(false);
                    await _delay(TimeSpan.FromSeconds(SpringSeconds), token).ConfigureAwait(false);
                    var end = await _output.ReadPositionAsync(token).ConfigureAwait(false);
                    TrimMeasured = _trimStart is { } a && end is { } b ? Math.Round(b.Y - a.Y, 3) : null;
                    Show(FfbWizardStep.AskTrim);
                    return;
            }
        }
        catch (OperationCanceledException)
        {
            // CancelAsync already stopped the forces and showed the step.
        }
        catch (Exception ex)
        {
            await _output.StopAsync().ConfigureAwait(false);
            Notes.Add("The test stopped: " + ex.Message);
            Show(FfbWizardStep.Failed);
        }
    }

    /// <summary>The user's answer to the current question.</summary>
    public async Task AnswerAsync(FfbWizardAnswer answer)
    {
        if (!Choices.Any(c => c.Answer == answer))
            return;
        switch (Step)
        {
            case FfbWizardStep.AskRight:
                FoundInvertX = answer switch { FfbWizardAnswer.Right => false, FfbWizardAnswer.Left => true, _ => null };
                if (answer == FfbWizardAnswer.Nothing)
                    Notes.Add("Left/right: the stick did not move. Is the right device picked, is it switched on, and is its force not set to 0 in its own tool? The left/right setting stays as it was.");
                Show(FfbWizardStep.ReadyForward);
                break;
            case FfbWizardStep.AskForward:
                FoundInvertY = answer switch { FfbWizardAnswer.Forward => false, FfbWizardAnswer.Back => true, _ => null };
                if (answer == FfbWizardAnswer.Nothing)
                    Notes.Add("Forward/back: the stick did not move. The forward/back setting stays as it was.");
                Show(FfbWizardStep.ReadySpring);
                break;
            case FfbWizardStep.AskSpring:
                SpringWorks = answer == FfbWizardAnswer.Yes;
                await _output.StopAsync().ConfigureAwait(false);
                if (SpringWorks == false)
                    Notes.Add("Centring: the stick did not return to the middle with JoyPro's spring.");
                Show(FfbWizardStep.ReadyTrim);
                break;
            case FfbWizardStep.AskTrim:
                TrimWorks = answer == FfbWizardAnswer.Yes;
                await _output.StopAsync().ConfigureAwait(false);
                if (TrimWorks == false || SpringWorks == false)
                    Notes.Add(IsVpforce ? VpforceHint : GenericSpringHint);
                else if (TrimMeasured is { } moved && Math.Abs(moved) < MovedThreshold)
                    Notes.Add("JoyPro measured almost no movement during the trim check (" + Percent(moved) + "); if the stick did not really move, see the device's own spring setting.");
                Show(FfbWizardStep.Done);
                break;
        }
    }

    /// <summary>Stops every force and ends the test. Safe in every step, also twice.</summary>
    public async Task CancelAsync()
    {
        if (Finished)
            return;
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        await _output.StopAsync().ConfigureAwait(false);
        Show(FfbWizardStep.Cancelled);
    }

    /// <summary>A step can be repeated now: after a push, the spring or the trim check (the question is on screen).</summary>
    public bool CanRetry => Step is FfbWizardStep.AskRight or FfbWizardStep.AskForward or FfbWizardStep.AskSpring or FfbWizardStep.AskTrim;

    /// <summary>"Start over" makes sense once the test has left the intro.</summary>
    public bool CanStartOver => Step != FfbWizardStep.Intro;

    /// <summary>Repeats the step whose question is shown: stops the forces and announces the same push / spring / trim
    /// again (the user clicks Ready). Earlier answers stay.</summary>
    public async Task RetryAsync()
    {
        if (!CanRetry)
            return;
        var again = Step switch
        {
            FfbWizardStep.AskRight => FfbWizardStep.ReadyRight,
            FfbWizardStep.AskForward => FfbWizardStep.ReadyForward,
            FfbWizardStep.AskSpring => FfbWizardStep.ReadySpring,
            _ => FfbWizardStep.ReadyTrim
        };
        await _output.StopAsync().ConfigureAwait(false);
        if (again == FfbWizardStep.ReadyTrim)
            TrimMeasured = null;
        Show(again);
    }

    /// <summary>Stops every force (also a running push) and starts again from the first step, forgetting the answers.</summary>
    public async Task StartOverAsync()
    {
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        await _output.StopAsync().ConfigureAwait(false);
        Restart();
    }

    /// <summary>Starts again from the first step (after Done, Cancelled or Failed).</summary>
    public void Restart()
    {
        _cts = new CancellationTokenSource();
        FoundInvertX = FoundInvertY = null;
        SpringWorks = TrimWorks = null;
        TrimMeasured = null;
        Notes.Clear();
        Show(FfbWizardStep.Intro);
    }

    private async Task RunPushAsync(FfbAxis axis, double magnitude, CancellationToken token)
    {
        try
        {
            await _output.PushAsync(axis, Math.Clamp(magnitude, -MaxTestForce, MaxTestForce), token).ConfigureAwait(false);
            await _delay(TimeSpan.FromSeconds(PushSeconds), token).ConfigureAwait(false);
        }
        finally
        {
            await _output.StopAsync().ConfigureAwait(false);
        }
    }

    private static string Percent(double value) => (value * 100).ToString("0", CultureInfo.InvariantCulture) + " %";

    private void Show(FfbWizardStep step)
    {
        Step = step;
        var seconds = PushSeconds.ToString("0.#", CultureInfo.InvariantCulture);
        (Title, Text, Icon, Choices) = step switch
        {
            FfbWizardStep.Intro => ("Direction test",
                "JoyPro checks which way your stick pushes, so forces and trim go the right way. It gently pushes the stick " +
                "(20 % force, " + seconds + " s each) and asks you what you saw. Hold the grip loosely or let go, and keep the " +
                "game's own force feedback off. You can cancel at any time; every force stops then." +
                (IsVpforce ? " Tip for VPforce sticks: the spring and trim checks at the end only work when the Configurator's own spring is off." : ""),
                FfbWizardIcons.Info, (IReadOnlyList<FfbWizardChoice>)[]),
            FfbWizardStep.ReadyRight => ("Step 1 of 4: push to the right",
                "Next, JoyPro pushes the stick to the RIGHT for " + seconds + " s. Watch the stick, then click Ready.",
                FfbWizardIcons.Right, []),
            FfbWizardStep.PushingRight => ("Pushing...", "The stick is being pushed now. Watch which way it goes.", FfbWizardIcons.Right, []),
            FfbWizardStep.AskRight => ("Which way did the stick move?", "JoyPro pushed it to the right.",
                FfbWizardIcons.Right,
                [new(FfbWizardAnswer.Right, "Right", FfbWizardIcons.Right), new(FfbWizardAnswer.Left, "Left", FfbWizardIcons.Left),
                 new(FfbWizardAnswer.Nothing, "It did not move", FfbWizardIcons.Nothing)]),
            FfbWizardStep.ReadyForward => ("Step 2 of 4: push forward",
                "Next, JoyPro pushes the stick FORWARD (away from you) for " + seconds + " s. Click Ready.",
                FfbWizardIcons.Forward, []),
            FfbWizardStep.PushingForward => ("Pushing...", "The stick is being pushed now. Watch which way it goes.", FfbWizardIcons.Forward, []),
            FfbWizardStep.AskForward => ("Which way did the stick move?", "JoyPro pushed it forward (away from you).",
                FfbWizardIcons.Forward,
                [new(FfbWizardAnswer.Forward, "Forward (away from me)", FfbWizardIcons.Forward), new(FfbWizardAnswer.Back, "Back (towards me)", FfbWizardIcons.Back),
                 new(FfbWizardAnswer.Nothing, "It did not move", FfbWizardIcons.Nothing)]),
            FfbWizardStep.ReadySpring => ("Step 3 of 4: centring spring",
                "Next, JoyPro switches its centring spring on (25 % at most). Then move the stick to a side and let it go.",
                FfbWizardIcons.Spring, []),
            FfbWizardStep.SpringRunning => ("Spring on", "Switching the spring on...", FfbWizardIcons.Spring, []),
            FfbWizardStep.AskSpring => ("Does the stick return to the middle?",
                "The spring is on now. Move the stick to a side and let it go.",
                FfbWizardIcons.Spring,
                [new(FfbWizardAnswer.Yes, "Yes, it springs back", FfbWizardIcons.Yes), new(FfbWizardAnswer.No, "No, it stays / is slack", FfbWizardIcons.No)]),
            FfbWizardStep.ReadyTrim => ("Step 4 of 4: trim",
                "Next, JoyPro moves the spring's centre forward, as trim does (" + Percent(TrimCentre) + " of the travel). Let go of the stick and click Ready.",
                FfbWizardIcons.Trim, []),
            FfbWizardStep.TrimRunning => ("Trimming...", "The centre moves forward now; the stick should follow and stay there.", FfbWizardIcons.Trim, []),
            FfbWizardStep.AskTrim => ("Did the stick move forward and stay there?",
                TrimMeasured is { } moved ? "JoyPro measured a movement of " + Percent(moved) + " on the forward/back axis." : "JoyPro could not read the stick position.",
                FfbWizardIcons.Trim,
                [new(FfbWizardAnswer.Yes, "Yes", FfbWizardIcons.Yes), new(FfbWizardAnswer.No, "No", FfbWizardIcons.No)]),
            FfbWizardStep.Done => ("Done", Summary(), FfbWizardIcons.Done, []),
            FfbWizardStep.Cancelled => ("Cancelled", "The test was cancelled; every force is off. Nothing was changed.", FfbWizardIcons.Warning, []),
            _ => ("The test stopped", "Every force is off. " + string.Join(" ", Notes), FfbWizardIcons.Warning, [])
        };
        Changed?.Invoke();
    }

    private string Summary()
    {
        var parts = new List<string>
        {
            "Left/right: " + (FoundInvertX is null ? "unchanged" : InvertX ? "inverted" : "normal") + ".",
            "Forward/back: " + (FoundInvertY is null ? "unchanged" : InvertY ? "inverted" : "normal") + ".",
            "Centring spring: " + (SpringWorks == true ? "works" : "does not act") + ".",
            "Trim: " + (TrimWorks == true ? "works" : "does not move the stick") + "."
        };
        parts.AddRange(Notes);
        return string.Join(" ", parts);
    }
}
