using System.Collections.Generic;
using System.Linq;
using JoyPro.Gui.Services;
using Xunit;

namespace JoyPro.Gui.Tests;

// Record takes transitions only: a fake device whose 3-way switch rests in the middle (button 3) and whose mode
// switch rests on button 7.
public sealed class RecordButtonTrackerTests
{
    private const int Centred = -1;

    // A fake device: 16 buttons, one hat; Report() gives its current state the way DirectInput shows it.
    private sealed class FakeDevice
    {
        private readonly bool[] buttons = new bool[16];
        private int hat = Centred;

        public FakeDevice(params int[] resting)
        {
            foreach (int button in resting)
                buttons[button - 1] = true;
        }

        public FakeDevice Press(int button)
        {
            buttons[button - 1] = true;
            return this;
        }

        public FakeDevice Release(int button)
        {
            buttons[button - 1] = false;
            return this;
        }

        public FakeDevice Hat(int angle)
        {
            hat = angle;
            return this;
        }

        public bool[] Buttons => buttons.ToArray();

        public int[] Hats => [hat];

        public static bool[] Empty => new bool[16];
    }

    // Opened before its first report: DirectInput shows nothing pressed.
    private static RecordButtonTracker OpenUnreported(bool allowRelease = false) => new(FakeDevice.Empty, [Centred], allowRelease);

    private static string? Feed(RecordButtonTracker tracker, FakeDevice device, bool armed = true) => tracker.Feed(device.Buttons, device.Hats, armed);

    [Fact]
    public void RestingButtonsInTheFirstReportDuringTheWarmup_NeverCount()
    {
        FakeDevice device = new(3, 7);
        RecordButtonTracker tracker = OpenUnreported();

        Assert.Null(Feed(tracker, device, armed: false));   // the first report arrives during the warmup
        Assert.Equal([2, 6], tracker.HeldAtStart.OrderBy(v => v));
        Assert.Null(Feed(tracker, device));
        Assert.Equal("JOY_BTN5", Feed(tracker, device.Press(5)));
    }

    [Fact]
    public void RestingButtonsInAFirstReportAfterTheWarmup_AreNotTakenForThePress()
    {
        // The old capture took the lowest new button of the first report: the resting middle of the switch.
        FakeDevice device = new(3, 7);
        RecordButtonTracker tracker = OpenUnreported();
        Assert.Null(Feed(tracker, new FakeDevice()));   // nothing yet

        // The user's press is what makes the device report; its resting buttons come along.
        Assert.Null(Feed(tracker, device.Press(5)));
        Assert.True(tracker.Reported);
        // The press is confirmed by its release; the resting buttons are never released.
        Assert.Equal("JOY_BTN5", Feed(tracker, device.Release(5)));
    }

    [Fact]
    public void AFirstReportFromAnAxisTwitch_ThenTheRealPressCountsAtOnce()
    {
        FakeDevice device = new(3, 7);
        RecordButtonTracker tracker = OpenUnreported();
        Assert.Null(Feed(tracker, device));             // the resting buttons show up with the first report
        Assert.Null(Feed(tracker, device));
        Assert.Equal("JOY_BTN12", Feed(tracker, device.Press(12)));
    }

    [Fact]
    public void ButtonsHeldAtTheStart_CountOnlyWhenPressedAgain()
    {
        FakeDevice device = new(3, 7);
        RecordButtonTracker tracker = new(device.Buttons, [Centred]);   // opened with a real report
        Assert.True(tracker.Reported);

        Assert.Null(Feed(tracker, device.Release(7)));  // leaving the mode switch's resting position is not a press
        Assert.Equal("JOY_BTN7", Feed(tracker, device.Press(7)));
    }

    [Fact]
    public void MovingTheThreeWaySwitch_TakesTheNewPositionNotTheOldOne()
    {
        FakeDevice device = new(3);
        RecordButtonTracker tracker = new(device.Buttons, [Centred]);
        // Break before make: the middle goes off in one report, the upper position comes on in the next.
        Assert.Null(Feed(tracker, device.Release(3)));
        Assert.Equal("JOY_BTN2", Feed(tracker, device.Press(2)));
    }

    [Fact]
    public void ReleasingARestingButton_CountsOnlyWhenEnabled()
    {
        FakeDevice device = new(3);
        RecordButtonTracker on = new(device.Buttons, [Centred], allowReleaseOfHeld: true);
        Assert.Equal("JOY_BTN3", Feed(on, device.Release(3)));

        FakeDevice other = new(3);
        RecordButtonTracker off = new(other.Buttons, [Centred]);
        Assert.Null(Feed(off, other.Release(3)));
    }

    [Fact]
    public void AButtonPressedDuringTheWarmup_IsIgnoredUntilPressedAgain()
    {
        FakeDevice device = new();
        RecordButtonTracker tracker = new(device.Buttons, [Centred]);
        Assert.Null(Feed(tracker, device.Press(4), armed: false));
        Assert.Null(Feed(tracker, device));
        Assert.Null(Feed(tracker, device.Release(4)));
        Assert.Equal("JOY_BTN4", Feed(tracker, device.Press(4)));
    }

    [Fact]
    public void HatDirections_CountWhenTheyChange()
    {
        FakeDevice device = new(3);
        RecordButtonTracker tracker = OpenUnreported();
        Assert.Equal("JOY_BTN_POV1_R", Feed(tracker, device.Hat(9000)));    // a hat never rests off centre
        Assert.Equal("JOY_BTN_POV1_DR", Feed(tracker, device.Hat(13500)));
        Assert.Null(Feed(tracker, device.Hat(Centred)));
        Assert.Equal("JOY_BTN_POV1_U", Feed(tracker, device.Hat(35990)));    // snapped: 359.9 degrees is up
    }

    [Theory]
    [InlineData(0, "U")]
    [InlineData(4500, "UR")]
    [InlineData(31500, "UL")]
    [InlineData(-1, null)]
    [InlineData(65535, null)]
    public void PovDirection_NamesTheEightDirections(int angle, string? expected) =>
        Assert.Equal(expected, RecordButtonTracker.PovDirection(angle));

    [Fact]
    public void ButtonsBeyond32_AreNamed()
    {
        bool[] buttons = new bool[128];
        RecordButtonTracker tracker = new(buttons, [Centred, Centred, Centred, Centred]);
        Assert.Null(tracker.Feed(buttons, [Centred, Centred, Centred, Centred], armed: false));
        List<bool> pressed = buttons.ToList();
        pressed[99] = true;
        Assert.Null(tracker.Feed(pressed, [Centred, Centred, Centred, Centred], armed: true));  // first report
        pressed[99] = false;
        Assert.Equal("JOY_BTN100", tracker.Feed(pressed, [Centred, Centred, Centred, Centred], armed: true));
    }
}
