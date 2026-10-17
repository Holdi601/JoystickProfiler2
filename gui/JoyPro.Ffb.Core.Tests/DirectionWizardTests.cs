using JoyPro.Ffb.Core.Host;
using JoyPro.Ffb.Core.Output;

namespace JoyPro.Ffb.Core.Tests;

/// <summary>The direction test's state machine with a fake output (nothing reaches hardware).</summary>
public class DirectionWizardTests
{
    private sealed class FakeOutput : IFfbTestOutput
    {
        public readonly List<string> Calls = [];
        public (double X, double Y)? Position = (0, 0);
        public (double X, double Y)? PositionAfterSpring;
        public bool FailOnPush;
        private bool _springOn;

        /// <summary>True while a force or spring was started and not stopped since.</summary>
        public bool ForceOn { get; private set; }

        public Task SetInvertAsync(bool invertX, bool invertY, CancellationToken token)
        {
            Calls.Add($"invert {(invertX ? 1 : 0)} {(invertY ? 1 : 0)}");
            return Task.CompletedTask;
        }

        public Task PushAsync(FfbAxis axis, double magnitude, CancellationToken token)
        {
            if (FailOnPush)
                throw new InvalidOperationException("device busy");
            Calls.Add(FormattableString.Invariant($"push {axis} {magnitude:0.##}"));
            ForceOn = true;
            return Task.CompletedTask;
        }

        public Task SpringAsync(double stiffness, double centreX, double centreY, CancellationToken token)
        {
            Calls.Add(FormattableString.Invariant($"spring {stiffness:0.##} {centreX:0.##} {centreY:0.##}"));
            ForceOn = true;
            _springOn = true;
            return Task.CompletedTask;
        }

        public Task<(double X, double Y)?> ReadPositionAsync(CancellationToken token)
        {
            Calls.Add("pos");
            return Task.FromResult(_springOn && PositionAfterSpring is not null && Calls.Count(c => c == "pos") > 1 ? PositionAfterSpring : Position);
        }

        public Task StopAsync()
        {
            Calls.Add("stop");
            ForceOn = false;
            _springOn = false;
            return Task.CompletedTask;
        }
    }

    private static readonly Func<TimeSpan, CancellationToken, Task> NoWait = (_, token) =>
    {
        token.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    };

    private static async Task<FfbDirectionWizard> RunToAsk(FakeOutput output, bool vpforce = false, bool invertX = false, bool invertY = false)
    {
        var wizard = new FfbDirectionWizard(output, invertX, invertY, vpforce, NoWait);
        Assert.Equal(FfbWizardStep.Intro, wizard.Step);
        await wizard.ReadyAsync();                   // Intro -> announce the right push
        Assert.Equal(FfbWizardStep.ReadyRight, wizard.Step);
        Assert.True(wizard.WaitingForReady);
        await wizard.ReadyAsync();
        return wizard;
    }

    [Fact]
    public async Task RhinoAnswers_GiveInvertYOnly_AndForcesAlwaysStop()
    {
        var output = new FakeOutput { PositionAfterSpring = (0, -0.3) };
        var wizard = await RunToAsk(output, vpforce: true);
        Assert.Equal(FfbWizardStep.AskRight, wizard.Step);
        Assert.False(output.ForceOn);                // the push stopped before the question
        Assert.Contains("push X 0.2", output.Calls);
        Assert.Equal("invert 0 0", output.Calls[0]); // raw directions for the pushes

        await wizard.AnswerAsync(FfbWizardAnswer.Right);
        Assert.Equal(FfbWizardStep.ReadyForward, wizard.Step);
        await wizard.ReadyAsync();
        Assert.Contains("push Y -0.2", output.Calls);  // forward = negative Y
        Assert.Equal(FfbWizardStep.AskForward, wizard.Step);
        await wizard.AnswerAsync(FfbWizardAnswer.Back);  // the Rhino: a forward push went back
        Assert.Equal((false, true), (wizard.InvertX, wizard.InvertY));

        Assert.Equal(FfbWizardStep.ReadySpring, wizard.Step);
        await wizard.ReadyAsync();
        Assert.Equal("invert 0 1", output.Calls[^2]);    // spring and trim checks with the found inverts
        Assert.Equal(FfbWizardStep.AskSpring, wizard.Step);
        Assert.True(output.ForceOn);                 // the spring stays on while the user tries it
        await wizard.AnswerAsync(FfbWizardAnswer.Yes);
        Assert.False(output.ForceOn);

        Assert.Equal(FfbWizardStep.ReadyTrim, wizard.Step);
        await wizard.ReadyAsync();
        Assert.Contains(output.Calls, c => c.StartsWith("spring 0.5 0 -0.35", StringComparison.Ordinal));
        Assert.Equal(FfbWizardStep.AskTrim, wizard.Step);
        Assert.Equal(-0.3, wizard.TrimMeasured);
        await wizard.AnswerAsync(FfbWizardAnswer.Yes);
        Assert.False(output.ForceOn);
        Assert.Equal(FfbWizardStep.Done, wizard.Step);
        Assert.True(wizard.Finished);
        Assert.Contains("Forward/back: inverted", wizard.Text);
        Assert.DoesNotContain(FfbDirectionWizard.VpforceHint, wizard.Text);
    }

    [Fact]
    public async Task LeftAndNothing_InvertXAndKeepY()
    {
        var output = new FakeOutput();
        var wizard = await RunToAsk(output, invertY: true);
        await wizard.AnswerAsync(FfbWizardAnswer.Left);
        await wizard.ReadyAsync();
        await wizard.AnswerAsync(FfbWizardAnswer.Nothing);
        Assert.Equal(true, wizard.FoundInvertX);
        Assert.Null(wizard.FoundInvertY);
        Assert.True(wizard.InvertY);                 // unchanged: the device's current setting
        Assert.Contains(wizard.Notes, n => n.Contains("did not move", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SpringOrTrimNotWorking_OnAVpforce_ShowsTheConfiguratorHint()
    {
        var output = new FakeOutput { PositionAfterSpring = (0, 0) };
        var wizard = await RunToAsk(output, vpforce: true);
        await wizard.AnswerAsync(FfbWizardAnswer.Right);
        await wizard.ReadyAsync();
        await wizard.AnswerAsync(FfbWizardAnswer.Forward);
        await wizard.ReadyAsync();
        await wizard.AnswerAsync(FfbWizardAnswer.No);
        await wizard.ReadyAsync();
        Assert.Equal(0, wizard.TrimMeasured);
        await wizard.AnswerAsync(FfbWizardAnswer.No);
        Assert.Equal(FfbWizardStep.Done, wizard.Step);
        Assert.Contains(FfbDirectionWizard.VpforceHint, wizard.Notes);
        Assert.Contains("Configurator", wizard.Text);
        Assert.False(output.ForceOn);
    }

    [Fact]
    public async Task OtherDevices_GetTheGenericHint()
    {
        var output = new FakeOutput();
        var wizard = await RunToAsk(output);
        await wizard.AnswerAsync(FfbWizardAnswer.Right);
        await wizard.ReadyAsync();
        await wizard.AnswerAsync(FfbWizardAnswer.Forward);
        await wizard.ReadyAsync();
        await wizard.AnswerAsync(FfbWizardAnswer.Yes);
        await wizard.ReadyAsync();
        await wizard.AnswerAsync(FfbWizardAnswer.No);
        Assert.Contains(FfbDirectionWizard.GenericSpringHint, wizard.Notes);
    }

    [Fact]
    public async Task Cancel_InAnyStep_StopsTheForces_AndChangesNothing()
    {
        var output = new FakeOutput();
        var wizard = await RunToAsk(output);
        await wizard.AnswerAsync(FfbWizardAnswer.Left);
        await wizard.ReadyAsync();
        await wizard.AnswerAsync(FfbWizardAnswer.Back);
        await wizard.ReadyAsync();                   // spring on
        Assert.True(output.ForceOn);
        await wizard.CancelAsync();
        Assert.False(output.ForceOn);
        Assert.Equal(FfbWizardStep.Cancelled, wizard.Step);
        await wizard.CancelAsync();                  // twice is fine
        await wizard.ReadyAsync();                   // ignored when finished
        Assert.Equal(FfbWizardStep.Cancelled, wizard.Step);

        wizard.Restart();
        Assert.Equal(FfbWizardStep.Intro, wizard.Step);
        Assert.Null(wizard.FoundInvertX);
    }

    [Fact]
    public async Task CancelDuringAPush_StopsAtOnce()
    {
        var output = new FakeOutput();
        var started = new TaskCompletionSource();
        var wizard = new FfbDirectionWizard(output, false, false, false, async (time, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });
        await wizard.ReadyAsync();
        var push = wizard.ReadyAsync();
        await started.Task;
        Assert.True(wizard.Busy);
        Assert.True(output.ForceOn);
        await wizard.CancelAsync();
        await push;
        Assert.False(output.ForceOn);
        Assert.Equal(FfbWizardStep.Cancelled, wizard.Step);
    }

    [Fact]
    public async Task Retry_RepeatsEachStep_AndStartOverBeginsAgain()
    {
        var output = new FakeOutput { PositionAfterSpring = (0, -0.3) };
        var wizard = await RunToAsk(output);
        Assert.True(wizard.CanRetry);
        await wizard.RetryAsync();                    // the right push once more
        Assert.Equal(FfbWizardStep.ReadyRight, wizard.Step);
        await wizard.ReadyAsync();
        Assert.Equal(2, output.Calls.Count(c => c == "push X 0.2"));
        await wizard.AnswerAsync(FfbWizardAnswer.Left);
        await wizard.ReadyAsync();
        await wizard.RetryAsync();                    // forward push again; the left/right answer stays
        Assert.Equal(FfbWizardStep.ReadyForward, wizard.Step);
        Assert.Equal(true, wizard.FoundInvertX);
        await wizard.ReadyAsync();
        await wizard.AnswerAsync(FfbWizardAnswer.Back);
        await wizard.ReadyAsync();                    // spring on
        Assert.True(output.ForceOn);
        await wizard.RetryAsync();                    // spring step again: stopped first
        Assert.False(output.ForceOn);
        Assert.Equal(FfbWizardStep.ReadySpring, wizard.Step);
        await wizard.ReadyAsync();
        await wizard.AnswerAsync(FfbWizardAnswer.Yes);
        await wizard.ReadyAsync();
        Assert.Equal(FfbWizardStep.AskTrim, wizard.Step);
        await wizard.RetryAsync();
        Assert.Equal(FfbWizardStep.ReadyTrim, wizard.Step);
        Assert.Null(wizard.TrimMeasured);
        Assert.False(wizard.CanRetry);                // nothing to repeat before Ready

        await wizard.StartOverAsync();
        Assert.Equal(FfbWizardStep.Intro, wizard.Step);
        Assert.Null(wizard.FoundInvertX);
        Assert.False(output.ForceOn);
        Assert.False(wizard.CanStartOver);
        await wizard.ReadyAsync();                    // works again after starting over
        await wizard.ReadyAsync();
        Assert.Equal(FfbWizardStep.AskRight, wizard.Step);
    }

    [Fact]
    public async Task StartOverDuringAPush_StopsAtOnce()
    {
        var output = new FakeOutput();
        var started = new TaskCompletionSource();
        var wizard = new FfbDirectionWizard(output, false, false, false, async (time, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });
        await wizard.ReadyAsync();
        var push = wizard.ReadyAsync();
        await started.Task;
        await wizard.StartOverAsync();
        await push;
        Assert.False(output.ForceOn);
        Assert.Equal(FfbWizardStep.Intro, wizard.Step);
    }

    [Fact]
    public async Task AnOutputError_EndsTheTestWithForcesOff()
    {
        var output = new FakeOutput { FailOnPush = true };
        var wizard = new FfbDirectionWizard(output, false, false, false, NoWait);
        await wizard.ReadyAsync();
        await wizard.ReadyAsync();
        Assert.Equal(FfbWizardStep.Failed, wizard.Step);
        Assert.Contains("device busy", wizard.Text);
        Assert.Equal("stop", output.Calls[^1]);
    }

    [Fact]
    public async Task AnswersThatAreNotOffered_AreIgnored_AndForcesStayLow()
    {
        var output = new FakeOutput();
        var wizard = await RunToAsk(output);
        await wizard.AnswerAsync(FfbWizardAnswer.Forward);   // not a left/right answer
        Assert.Equal(FfbWizardStep.AskRight, wizard.Step);
        Assert.All(wizard.Choices, c => Assert.False(string.IsNullOrWhiteSpace(c.Icon)));
        Assert.True(FfbDirectionWizard.PushMagnitude <= FfbDirectionWizard.MaxTestForce);
        Assert.True(FfbDirectionWizard.MaxTestForce <= 0.25);
    }
}
