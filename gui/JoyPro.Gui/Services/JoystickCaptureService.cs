using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using JoyPro.Logging;

namespace JoyPro.Gui.Services;

[SupportedOSPlatform("windows")]
public sealed class JoystickCaptureService : IJoystickCaptureService
{
    private const uint DirectInputVersion = 0x0800;
    private const uint Di8DevclassGameCtrl = 4;
    private const uint DiedflAttachedOnly = 0x00000001;
    private const uint DisclBackground = 0x00000008;
    private const uint DisclForeground = 0x00000004;
    private const uint DisclNonExclusive = 0x00000002;
    private const uint DidfAbsAxis = 0x00000001;
    private const uint DidftAxis = 0x00000003;
    private const uint DidftButton = 0x0000000C;
    private const uint DidftPov = 0x00000010;
    private const uint DidftOptional = 0x80000000;
    private const int JoyErrNoError = 0;
    private const int JoyErrUnplugged = 167;
    private const uint JoyReturnX = 0x00000001;
    private const uint JoyReturnY = 0x00000002;
    private const uint JoyReturnZ = 0x00000004;
    private const uint JoyReturnR = 0x00000008;
    private const uint JoyReturnU = 0x00000010;
    private const uint JoyReturnV = 0x00000020;
    private const uint JoyReturnPov = 0x00000040;
    private const uint JoyReturnButtons = 0x00000080;
    private const uint JoyReturnPovCts = 0x00000200;
    private const uint JoyReturnAll =
        JoyReturnX | JoyReturnY | JoyReturnZ |
        JoyReturnR | JoyReturnU | JoyReturnV |
        JoyReturnPov | JoyReturnButtons;
    private static readonly Guid IidIDirectInput8W = new("BF798031-483A-4DA2-AA99-5D64ED369700");
    private static readonly List<KeyValuePair<int, string>> KeyboardVirtualKeyMap = BuildKeyboardVirtualKeyMap();
    private static readonly List<DioObjectDataFormat> DirectInputObjectFormats = BuildDirectInputObjectFormats();
    private static readonly EnumDevicesCallback DirectInputDeviceEnumCallback = EnumDirectInputDevicesCallback;
    private static readonly IntPtr DirectInputDeviceEnumCallbackPtr = Marshal.GetFunctionPointerForDelegate(DirectInputDeviceEnumCallback);

    public string LastDiagnostics { get; private set; } = string.Empty;

    public sealed class CaptureOptions
    {
        public bool AxisMode { get; init; }
        public bool IncludeKeyboard { get; init; }
        public string PreferredDevice { get; init; } = string.Empty;
        public int TimeoutMs { get; init; } = 5000;
        public int WarmupMs { get; init; } = 300;
        public int PollWaitMs { get; init; } = 10;
        public int AxisThresholdPercent { get; init; } = 30;
        // A button that was already pressed when recording started (a resting switch position) counts when it is
        // released. Off: such buttons never count.
        public bool RecordReleaseOfHeldButtons { get; init; }
    }

    public sealed class CaptureResult
    {
        public string Device { get; init; } = string.Empty;
        public string Input { get; init; } = string.Empty;
        public DateTimeOffset CapturedAtUtc { get; init; } = DateTimeOffset.UtcNow;

        public override string ToString()
        {
            return Device + " :: " + Input + " @ " + CapturedAtUtc.ToString("u", CultureInfo.InvariantCulture);
        }
    }

    // The 8 DirectInput axes in DIJOYSTATE2 order, named as the engine and the games name them.
    internal static readonly string[] DirectInputAxisNames =
        ["JOY_X", "JOY_Y", "JOY_Z", "JOY_RX", "JOY_RY", "JOY_RZ", "JOY_SLIDER1", "JOY_SLIDER2"];

    // DirectInput's default range of an absolute axis.
    private const int DirectInputAxisMin = 0;
    private const int DirectInputAxisMax = 65535;

    public Task<CaptureResult?> CaptureNextAsync(CaptureOptions options, CancellationToken cancellationToken)
    {
        // Polls off the UI thread (like MonitorAsync): DirectInput reads in the background and the window stays responsive.
        return Task.Run(() => CaptureNextCoreAsync(options, cancellationToken), CancellationToken.None);
    }

    private async Task<CaptureResult?> CaptureNextCoreAsync(CaptureOptions options, CancellationToken cancellationToken)
    {
        LastDiagnostics = string.Empty;
        CaptureOptions safeOptions = options ?? new CaptureOptions();
        bool axisMode = safeOptions.AxisMode;
        bool includeKeyboard = safeOptions.IncludeKeyboard && !axisMode;
        int timeoutMs = Math.Max(100, safeOptions.TimeoutMs);
        int warmupMs = Math.Max(0, safeOptions.WarmupMs);
        int pollWaitMs = Math.Max(5, safeOptions.PollWaitMs);
        int axisThresholdPercent = Math.Clamp(safeOptions.AxisThresholdPercent, 1, 100);
        string preferredDevice = (safeOptions.PreferredDevice ?? string.Empty).Trim();

        List<string> warnings = [];
        List<string> directInputDevices = DirectInputDeviceDiscovery.EnumerateConnectedDevices(warnings);
        // DirectInput for buttons, hats and axes (128 buttons, 8 axes incl. sliders, the names the profile uses).
        List<DirectInputPollingDevice> directInputPollingDevices = CreateDirectInputPollingDevices(preferredDevice, warnings);

        // WinMM only when DirectInput could not open a single device: it sees 32 buttons and 6 axes at most.
        List<WinMmDevice> devices = directInputPollingDevices.Count == 0 ? EnumerateWinMmDevices() : [];
        Dictionary<uint, string> resolvedDeviceNames = devices.ToDictionary(
            d => d.Id,
            d => ResolveBestDeviceName(d.ProductName, directInputDevices));
        List<WinMmDevice> scanDevices = devices
            .OrderByDescending(d => DeviceMatchesPreferred(
                resolvedDeviceNames.TryGetValue(d.Id, out string? resolved) ? resolved : d.ProductName,
                preferredDevice))
            .ToList();

        Dictionary<uint, JoyInfoEx> lastStates = new();
        foreach (WinMmDevice device in devices)
        {
            if (TryReadState(device.Id, out JoyInfoEx state))
                lastStates[device.Id] = state;
        }
        // Axes are judged against where they were when recording armed, not against the previous poll (10 ms ago):
        // nobody moves an axis 30 % within one poll.
        Dictionary<uint, JoyInfoEx> winMmBaselines = new(lastStates);

        string Diagnostics() => BuildDiagnosticsSummary(
            devices.Count,
            directInputDevices.Count,
            directInputPollingDevices.Count,
            warnings);

        JoyProLog.Info("Record", "Capture start: looking for " + (axisMode ? "an axis" : includeKeyboard ? "a button, hat or key" : "a button or hat") +
                                 ", timeout " + timeoutMs.ToString(CultureInfo.InvariantCulture) + " ms, warmup " +
                                 warmupMs.ToString(CultureInfo.InvariantCulture) + " ms" +
                                 (axisMode ? ", axis threshold " + axisThresholdPercent.ToString(CultureInfo.InvariantCulture) + " %" : string.Empty) +
                                 (preferredDevice.Length > 0 ? ", preferred device " + preferredDevice : string.Empty) + "; " + Diagnostics() +
                                 "; devices: " + (directInputPollingDevices.Count > 0
                                     ? string.Join(", ", directInputPollingDevices.Select(d => d.DisplayName))
                                     : string.Join(", ", scanDevices.Select(d => "WinMM " + d.ProductName))));

        if (lastStates.Count == 0 && !includeKeyboard && directInputPollingDevices.Count == 0)
        {
            LastDiagnostics = Diagnostics();
            JoyProLog.Warn("Record", "Capture: no joystick could be opened (DirectInput and WinMM); " + LastDiagnostics);
            return null;
        }
        Dictionary<int, bool> lastKeyboardStates = includeKeyboard
            ? CaptureKeyboardStates()
            : new Dictionary<int, bool>();

        Stopwatch clock = Stopwatch.StartNew();
        int ignoredButtons = 0;
        bool allowRelease = safeOptions.RecordReleaseOfHeldButtons;
        foreach (DirectInputPollingDevice device in directInputPollingDevices)
            device.Buttons = new RecordButtonTracker(PressedButtons(device.LastState.Buttons), PovAngles(device.LastState.Pov), allowRelease);
        Dictionary<uint, RecordButtonTracker> winMmTrackers = lastStates.ToDictionary(
            entry => entry.Key,
            entry => new RecordButtonTracker(PressedButtons(entry.Value.Buttons), [WinMmPovAngle(entry.Value.Pov)], allowRelease));

        CaptureResult Found(string device, string input)
        {
            LastDiagnostics = Diagnostics();
            JoyProLog.Info("Record", "Captured " + device + " :: " + input + " after " +
                                     clock.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms");
            return new CaptureResult
            {
                Device = device,
                Input = input
            };
        }

        try
        {
            while (clock.ElapsedMilliseconds <= timeoutMs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool armed = clock.ElapsedMilliseconds >= warmupMs;

                if (includeKeyboard)
                {
                    Dictionary<int, bool> currentKeyboardStates = CaptureKeyboardStates();
                    if (armed && TryDetectKeyboardEdge(lastKeyboardStates, currentKeyboardStates, out string keyboardInput))
                        return Found("Keyboard", keyboardInput);

                    lastKeyboardStates = currentKeyboardStates;
                }

                foreach (DirectInputPollingDevice device in directInputPollingDevices)
                {
                    if (!TryReadDirectInputState(device.Device, out DirectInputJoyState currentState))
                        continue;

                    if (axisMode)
                    {
                        int[] axes = AxisValues(currentState);
                        if (device.BaselineAxes == null)
                        {
                            device.BaselineAxes = axes;
                        }
                        else if (!armed)
                        {
                            device.AxesReported |= !axes.SequenceEqual(device.BaselineAxes);
                            device.BaselineAxes = axes;
                        }
                        else
                        {
                            // A device that sends reports only on change shows DirectInput's centred defaults until its
                            // first report; then every axis jumps to where it really is (a throttle at idle: 50 %).
                            // That first change re-centres the jumped axes instead of recording one of them.
                            if (!device.AxesReported && !axes.SequenceEqual(device.BaselineAxes))
                            {
                                device.AxesReported = true;
                                if (RebaseJumpedAxes(device.BaselineAxes, axes, DirectInputAxisMin, DirectInputAxisMax, axisThresholdPercent))
                                    JoyProLog.Info("Record", "First report of " + device.DisplayName + ": axes re-centred at their real position");
                            }

                            string? axisInput = DetectAxisMove(device.BaselineAxes, axes, DirectInputAxisMin, DirectInputAxisMax, axisThresholdPercent);
                            if (axisInput != null)
                                return Found(device.DisplayName, axisInput);
                        }

                        // A button on an axis relation does nothing; say so in the log, it is the usual "Record does nothing".
                        if (armed && ignoredButtons < 3 && TryDetectButtonOrPov(device.LastState, currentState, out string ignored))
                        {
                            ignoredButtons++;
                            JoyProLog.Info("Record", "Ignored " + device.DisplayName + " :: " + ignored + ": the relation is an axis, move an axis");
                        }
                    }
                    else if (device.Buttons!.Feed(PressedButtons(currentState.Buttons), PovAngles(currentState.Pov), armed) is { } directInputValue)
                    {
                        return Found(device.DisplayName, directInputValue);
                    }

                    device.LastState = currentState;
                }

                foreach (WinMmDevice device in scanDevices)
                {
                    if (!lastStates.TryGetValue(device.Id, out JoyInfoEx previous))
                        continue;
                    if (!TryReadState(device.Id, out JoyInfoEx current))
                        continue;

                    string name = resolvedDeviceNames.TryGetValue(device.Id, out string? resolved) ? resolved : device.ProductName;
                    if (!axisMode && winMmTrackers.TryGetValue(device.Id, out RecordButtonTracker? tracker) &&
                        tracker.Feed(PressedButtons(current.Buttons), [WinMmPovAngle(current.Pov)], armed) is { } buttonInput)
                        return Found(name, buttonInput);

                    if (!armed)
                    {
                        winMmBaselines[device.Id] = current;
                    }
                    else
                    {

                        if (axisMode &&
                            winMmBaselines.TryGetValue(device.Id, out JoyInfoEx baseline) &&
                            TryDetectAxis(baseline, current, device.Caps, axisThresholdPercent, out string axisInput))
                            return Found(name, axisInput);
                    }

                    lastStates[device.Id] = current;
                }

                await Task.Delay(pollWaitMs, cancellationToken);
            }

            LastDiagnostics = Diagnostics();
            JoyProLog.Info("Record", "Capture timed out after " + timeoutMs.ToString(CultureInfo.InvariantCulture) + " ms without " +
                                     (axisMode ? "an axis moving " + axisThresholdPercent.ToString(CultureInfo.InvariantCulture) + " %" : "a press") +
                                     "; " + LastDiagnostics);
            return null;
        }
        catch (OperationCanceledException)
        {
            LastDiagnostics = Diagnostics();
            JoyProLog.Info("Record", "Capture cancelled after " + clock.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms");
            throw;
        }
        finally
        {
            foreach (DirectInputPollingDevice device in directInputPollingDevices)
                device.Dispose();
        }
    }

    private static int[] AxisValues(DirectInputJoyState state)
    {
        int[] sliders = state.Slider ?? [0, 0];
        return [state.X, state.Y, state.Z, state.Rx, state.Ry, state.Rz, sliders.Length > 0 ? sliders[0] : 0, sliders.Length > 1 ? sliders[1] : 0];
    }

    private static bool[] PressedButtons(byte[]? buttons)
    {
        if (buttons == null)
            return [];
        bool[] pressed = new bool[buttons.Length];
        for (int i = 0; i < buttons.Length; i++)
            pressed[i] = buttons[i] != 0;
        return pressed;
    }

    private static bool[] PressedButtons(uint winMmButtons)
    {
        bool[] pressed = new bool[32];
        for (int bit = 0; bit < 32; bit++)
            pressed[bit] = (winMmButtons & (1u << bit)) != 0;
        return pressed;
    }

    // DirectInput hat values as angles, -1 when centred.
    private static int[] PovAngles(uint[]? povs)
    {
        if (povs == null)
            return [];
        int[] angles = new int[povs.Length];
        for (int i = 0; i < povs.Length; i++)
            angles[i] = povs[i] > 35999 ? -1 : (int)povs[i];
        return angles;
    }

    private static int WinMmPovAngle(uint pov) => pov > 35999 ? -1 : (int)pov;

    // Sets the baseline of every axis that is already a full threshold away to its current value; true when one was.
    internal static bool RebaseJumpedAxes(int[] baseline, IReadOnlyList<int> current, int min, int max, int thresholdPercent)
    {
        double threshold = ((double)max - min) * Math.Clamp(thresholdPercent, 1, 100) / 100.0;
        bool any = false;
        for (int i = 0; i < Math.Min(baseline.Length, current.Count); i++)
        {
            if (Math.Abs((double)current[i] - baseline[i]) >= threshold)
            {
                baseline[i] = current[i];
                any = true;
            }
        }
        return any;
    }

    // The axis (DirectInputAxisNames) that moved furthest from its baseline, when that is at least thresholdPercent of
    // the range; null when none did. Furthest wins, so a stick's other axis twitching along does not.
    internal static string? DetectAxisMove(IReadOnlyList<int> baseline, IReadOnlyList<int> current, int min, int max, int thresholdPercent)
    {
        if (baseline == null || current == null)
            return null;
        double range = (double)max - min;
        if (range < 1)
            return null;

        double threshold = range * Math.Clamp(thresholdPercent, 1, 100) / 100.0;
        int best = -1;
        double bestDiff = 0;
        int count = Math.Min(Math.Min(baseline.Count, current.Count), DirectInputAxisNames.Length);
        for (int i = 0; i < count; i++)
        {
            double diff = Math.Abs((double)current[i] - baseline[i]);
            if (diff >= threshold && diff > bestDiff)
            {
                best = i;
                bestDiff = diff;
            }
        }

        return best < 0 ? null : DirectInputAxisNames[best];
    }

    private static readonly (int Key, string Name)[] ModifierKeys =
    [
        (0xA0, "LShift"), (0xA1, "RShift"), (0xA2, "LCtrl"), (0xA3, "RCtrl"), (0xA4, "LAlt"), (0xA5, "RAlt")
    ];

    public Task MonitorAsync(Action<JoystickInputEvent> onEvent, Action<JoystickAxisSnapshot>? onAxes, CancellationToken cancellationToken)
    {
        return Task.Run(async () =>
        {
            List<string> warnings = [];
            List<DirectInputPollingDevice> devices = CreateDirectInputPollingDevices(string.Empty, warnings);
            LastDiagnostics = devices.Count + " device(s) watched" + (warnings.Count > 0 ? "; " + string.Join("; ", warnings) : string.Empty);
            Dictionary<int, bool> keys = ModifierKeys.ToDictionary(k => k.Key, k => IsVirtualKeyDown(k.Key));
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    foreach (DirectInputPollingDevice device in devices)
                    {
                        if (!TryReadDirectInputState(device.Device, out DirectInputJoyState state))
                            continue;
                        // The opening state is DirectInput's empty defaults; the first state that differs is the
                        // device's first report (resting buttons, a throttle's real position).
                        bool firstReport = !device.Reported && !SameDirectInputState(device.LastState, state);
                        if (firstReport)
                            device.Reported = true;
                        foreach ((string input, bool down) in DiffStates(device.LastState.Buttons, state.Buttons, device.LastState.Pov, state.Pov))
                            onEvent(new JoystickInputEvent(device.DisplayName, input, down, firstReport));
                        if (onAxes != null)
                        {
                            double[] axes = NormalizedAxes(state);
                            // About 0.1 % of travel: no snapshots for sensor noise.
                            if (device.LastAxes == null || firstReport || AxesMoved(device.LastAxes, axes, 0.001))
                            {
                                device.LastAxes = axes;
                                onAxes(new JoystickAxisSnapshot(device.DisplayName, axes, firstReport));
                            }
                        }
                        device.LastState = state;
                    }
                    foreach ((int key, string name) in ModifierKeys)
                    {
                        bool down = IsVirtualKeyDown(key);
                        if (down != keys[key])
                        {
                            keys[key] = down;
                            onEvent(new JoystickInputEvent("Keyboard", name, down));
                        }
                    }
                    try
                    {
                        await Task.Delay(15, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
            finally
            {
                foreach (DirectInputPollingDevice device in devices)
                    device.Dispose();
            }
        }, CancellationToken.None);
    }

    // Diagnostics: opens every device and returns the pressed buttons of its opening state and of the state pollMs
    // later (shows whether a device reports its resting buttons at once or only with its first report).
    internal static List<(string Device, int[] AtOpen, int[] Later, long FirstChangeMs)> ProbePressedButtons(int pollMs)
    {
        List<(string, int[], int[], long)> output = [];
        List<DirectInputPollingDevice> devices = CreateDirectInputPollingDevices(string.Empty, []);
        try
        {
            Dictionary<DirectInputPollingDevice, long> firstChange = [];
            Stopwatch clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < Math.Max(0, pollMs))
            {
                foreach (DirectInputPollingDevice device in devices)
                {
                    if (!firstChange.ContainsKey(device) && TryReadDirectInputState(device.Device, out DirectInputJoyState polled) &&
                        (!polled.Buttons.SequenceEqual(device.LastState.Buttons) || !AxisValues(polled).SequenceEqual(AxisValues(device.LastState))))
                        firstChange[device] = clock.ElapsedMilliseconds;
                }
                Thread.Sleep(5);
            }
            foreach (DirectInputPollingDevice device in devices)
            {
                int[] atOpen = Pressed(device.LastState.Buttons);
                int[] later = TryReadDirectInputState(device.Device, out DirectInputJoyState state) ? Pressed(state.Buttons) : [];
                output.Add((device.DisplayName, atOpen, later, firstChange.TryGetValue(device, out long ms) ? ms : -1));
            }
        }
        finally
        {
            foreach (DirectInputPollingDevice device in devices)
                device.Dispose();
        }
        return output;

        static int[] Pressed(byte[]? buttons) => buttons == null ? [] : Enumerable.Range(0, buttons.Length).Where(i => buttons[i] != 0).Select(i => i + 1).ToArray();
    }

    private static bool SameDirectInputState(DirectInputJoyState a, DirectInputJoyState b) =>
        (a.Buttons ?? []).SequenceEqual(b.Buttons ?? []) && (a.Pov ?? []).SequenceEqual(b.Pov ?? []) && AxisValues(a).SequenceEqual(AxisValues(b));

    // DirectInput's default axis range is 0..65535.
    internal static double[] NormalizedAxes(int x, int y, int z, int rx, int ry, int rz, int slider1, int slider2)
    {
        static double Normalize(int value) => Math.Clamp(value / 65535.0, 0, 1);
        return [Normalize(x), Normalize(y), Normalize(z), Normalize(rx), Normalize(ry), Normalize(rz), Normalize(slider1), Normalize(slider2)];
    }

    private static double[] NormalizedAxes(DirectInputJoyState state)
    {
        int[] sliders = state.Slider ?? [0, 0];
        return NormalizedAxes(state.X, state.Y, state.Z, state.Rx, state.Ry, state.Rz, sliders.Length > 0 ? sliders[0] : 0, sliders.Length > 1 ? sliders[1] : 0);
    }

    internal static bool AxesMoved(double[] before, double[] after, double threshold)
    {
        for (int i = 0; i < Math.Min(before.Length, after.Length); i++)
        {
            if (Math.Abs(before[i] - after[i]) >= threshold)
                return true;
        }
        return false;
    }

    // Buttons and hat directions that went down or up between two states ("JOY_BTN3", "JOY_BTN_POV1_UR").
    internal static List<(string Input, bool Down)> DiffStates(IReadOnlyList<byte>? previousButtons, IReadOnlyList<byte>? currentButtons, IReadOnlyList<uint>? previousPov, IReadOnlyList<uint>? currentPov)
    {
        List<(string, bool)> changes = [];
        int buttons = Math.Min(previousButtons?.Count ?? 0, currentButtons?.Count ?? 0);
        for (int i = 0; i < buttons; i++)
        {
            bool was = previousButtons![i] != 0;
            bool now = currentButtons![i] != 0;
            if (was != now)
                changes.Add(("JOY_BTN" + (i + 1).ToString(CultureInfo.InvariantCulture), now));
        }
        int povs = Math.Min(previousPov?.Count ?? 0, currentPov?.Count ?? 0);
        for (int i = 0; i < povs; i++)
        {
            string? before = TryNormalizePovAngle(previousPov![i], out int a) ? PovSuffix(a) : null;
            string? after = TryNormalizePovAngle(currentPov![i], out int b) ? PovSuffix(b) : null;
            if (before == after)
                continue;
            string prefix = "JOY_BTN_POV" + (i + 1).ToString(CultureInfo.InvariantCulture) + "_";
            if (before != null)
                changes.Add((prefix + before, false));
            if (after != null)
                changes.Add((prefix + after, true));
        }
        return changes;
    }

    private static string BuildDiagnosticsSummary(
        int winMmDeviceCount,
        int directInputEnumeratedCount,
        int directInputPollingCount,
        List<string> warnings)
    {
        string summary = "diag: winmm=" + winMmDeviceCount.ToString(CultureInfo.InvariantCulture) +
                         ", directinput-enum=" + directInputEnumeratedCount.ToString(CultureInfo.InvariantCulture) +
                         ", directinput-active=" + directInputPollingCount.ToString(CultureInfo.InvariantCulture);
        if (warnings == null || warnings.Count == 0)
            return summary;

        string warningText = string.Join(" | ", warnings
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(warningText))
            return summary;

        return summary + " | warnings: " + warningText;
    }

    private static bool DeviceMatchesPreferred(string detectedDevice, string preferredDevice)
    {
        string preferred = NormalizeDeviceName(preferredDevice);
        if (preferred.Length == 0)
            return false;

        string detected = NormalizeDeviceName(detectedDevice);
        if (detected.Length == 0)
            return false;

        return string.Equals(detected, preferred, StringComparison.OrdinalIgnoreCase) ||
               detected.Contains(preferred, StringComparison.OrdinalIgnoreCase) ||
               preferred.Contains(detected, StringComparison.OrdinalIgnoreCase);
    }

    private static List<DirectInputPollingDevice> CreateDirectInputPollingDevices(string preferredDevice, List<string> warnings)
    {
        List<DirectInputPollingDevice> output = [];
        IDirectInput8W? directInput = null;
        GCHandle stateHandle = default;

        try
        {
            IntPtr hinst = GetModuleHandle(null);
            Guid iid = IidIDirectInput8W;
            int hr = DirectInput8Create(hinst, DirectInputVersion, ref iid, out directInput, IntPtr.Zero);
            if (hr != 0 || directInput == null)
            {
                warnings?.Add("DirectInput create failed: 0x" + hr.ToString("X8"));
                return output;
            }

            DirectInputEnumerationState state = new();
            stateHandle = GCHandle.Alloc(state);

            int enumHr = directInput.EnumDevices(
                Di8DevclassGameCtrl,
                DirectInputDeviceEnumCallbackPtr,
                GCHandle.ToIntPtr(stateHandle),
                DiedflAttachedOnly);
            if (enumHr != 0)
            {
                warnings?.Add("DirectInput enum failed: 0x" + enumHr.ToString("X8"));
                return output;
            }

            List<DideviceInstanceW> ordered = state.Instances
                .OrderByDescending(v => DeviceMatchesPreferred(
                    ToDirectInputInstanceString(v.tszInstanceName, v.guidInstance),
                    preferredDevice))
                .ToList();

            foreach (DideviceInstanceW instance in ordered)
            {
                Guid guid = instance.guidInstance;
                int createHr = directInput.CreateDevice(ref guid, out IDirectInputDevice8W device, IntPtr.Zero);
                if (createHr != 0 || device == null)
                    continue;

                bool keepDevice = false;
                try
                {
                    if (!TryConfigureDirectInputDevice(device, out string configError))
                    {
                        if (!string.IsNullOrWhiteSpace(configError))
                            warnings?.Add(configError);
                        continue;
                    }

                    if (!TryReadDirectInputState(device, out DirectInputJoyState initialState))
                    {
                        warnings?.Add("DirectInput initial state read failed for '" + (instance.tszInstanceName ?? string.Empty).Trim() + "'. Using lazy state initialization.");
                        initialState = CreateEmptyDirectInputState();
                    }

                    string displayName = ToDirectInputInstanceString(instance.tszInstanceName, instance.guidInstance);
                    if (string.IsNullOrWhiteSpace(displayName))
                        displayName = (instance.tszInstanceName ?? string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(displayName))
                        displayName = "DirectInput Device {" + instance.guidInstance.ToString().ToUpperInvariant() + "}";

                    output.Add(new DirectInputPollingDevice(displayName, device, initialState));
                    keepDevice = true;
                }
                finally
                {
                    if (!keepDevice && Marshal.IsComObject(device))
                        Marshal.ReleaseComObject(device);
                }
            }
        }
        catch (Exception ex)
        {
            warnings?.Add("DirectInput capture setup failed: " + ex.Message);
        }
        finally
        {
            if (stateHandle.IsAllocated)
                stateHandle.Free();
            if (directInput != null && Marshal.IsComObject(directInput))
                Marshal.ReleaseComObject(directInput);
        }

        return output;
    }

    private static int EnumDirectInputDevicesCallback(IntPtr lpddi, IntPtr pvRef)
    {
        if (lpddi == IntPtr.Zero || pvRef == IntPtr.Zero)
            return 1;

        try
        {
            GCHandle handle = GCHandle.FromIntPtr(pvRef);
            DirectInputEnumerationState? state = handle.Target as DirectInputEnumerationState;
            if (state == null)
                return 1;

            DideviceInstanceW instance = Marshal.PtrToStructure<DideviceInstanceW>(lpddi);
            string name = (instance.tszInstanceName ?? string.Empty).Trim();
            if (name.Length > 0)
                state.Instances.Add(instance);
        }
        catch
        {
            // Ignore and continue.
        }

        return 1;
    }

    private static List<DioObjectDataFormat> BuildDirectInputObjectFormats()
    {
        List<DioObjectDataFormat> items = [];

        // Match a DIJOYSTATE-style layout:
        // axes (0-31), POVs (32-47), buttons (48-175).
        // Axes by type GUID like c_dfDIJoystick2, not by instance number: instances count the axes a device has, so on a
        // stick without Z its Rz would land in the Z slot (and be recorded as JOY_Z).
        items.Add(MakeAxisObject(0, 0));   // X
        items.Add(MakeAxisObject(1, 4));   // Y
        items.Add(MakeAxisObject(2, 8));   // Z
        items.Add(MakeAxisObject(3, 12));  // RX
        items.Add(MakeAxisObject(4, 16));  // RY
        items.Add(MakeAxisObject(5, 20));  // RZ
        items.Add(MakeAxisObject(6, 24));  // Slider 1
        items.Add(MakeAxisObject(6, 28));  // Slider 2 (the next slider)

        items.Add(MakePovObject(0, 32));
        items.Add(MakePovObject(1, 36));
        items.Add(MakePovObject(2, 40));
        items.Add(MakePovObject(3, 44));

        // Buttons.
        for (int i = 0; i < 128; i++)
            items.Add(MakeButtonObject(i, 48 + i));

        return items;
    }

    // GUID_XAxis, GUID_YAxis, GUID_ZAxis, GUID_RxAxis, GUID_RyAxis, GUID_RzAxis, GUID_Slider in native memory that
    // lives as long as the process (SetDataFormat reads the pointers).
    // (A nested class: the formats are built by a static initializer above this line, which must not see Zero.)
    private static class AxisTypeGuids
    {
        public static readonly IntPtr Memory = AllocateAxisTypeGuids();
    }

    private static IntPtr AllocateAxisTypeGuids()
    {
        Guid[] guids =
        [
            new("A36D02E0-C9F3-11CF-BFC7-444553540000"),
            new("A36D02E1-C9F3-11CF-BFC7-444553540000"),
            new("A36D02E2-C9F3-11CF-BFC7-444553540000"),
            new("A36D02F4-C9F3-11CF-BFC7-444553540000"),
            new("A36D02F5-C9F3-11CF-BFC7-444553540000"),
            new("A36D02E3-C9F3-11CF-BFC7-444553540000"),
            new("A36D02E4-C9F3-11CF-BFC7-444553540000")
        ];
        IntPtr memory = Marshal.AllocHGlobal(guids.Length * 16);
        for (int i = 0; i < guids.Length; i++)
            Marshal.StructureToPtr(guids[i], memory + i * 16, false);
        return memory;
    }

    private const uint DidftAnyInstance = 0x00FFFF00;
    private const uint DidoiAspectPosition = 0x00000100;

    private static DioObjectDataFormat MakeAxisObject(int axisType, int offset)
    {
        return new DioObjectDataFormat
        {
            pguid = AxisTypeGuids.Memory + axisType * 16,
            dwOfs = (uint)offset,
            dwType = DidftOptional | DidftAxis | DidftAnyInstance,
            dwFlags = DidoiAspectPosition
        };
    }

    private static DioObjectDataFormat MakePovObject(int instance, int offset)
    {
        return new DioObjectDataFormat
        {
            pguid = IntPtr.Zero,
            dwOfs = (uint)offset,
            dwType = DidftOptional | DidftPov | ((uint)instance << 8),
            dwFlags = 0
        };
    }

    private static DioObjectDataFormat MakeButtonObject(int instance, int offset)
    {
        return new DioObjectDataFormat
        {
            pguid = IntPtr.Zero,
            dwOfs = (uint)offset,
            dwType = DidftOptional | DidftButton | ((uint)instance << 8),
            dwFlags = 0
        };
    }

    private static bool TryConfigureDirectInputDevice(IDirectInputDevice8W device, out string error)
    {
        error = string.Empty;
        GCHandle formatsHandle = default;
        try
        {
            // Use an explicit DIJOYSTATE2-compatible format so we can read up to 128 buttons.
            DioObjectDataFormat[] formats = DirectInputObjectFormats.ToArray();
            formatsHandle = GCHandle.Alloc(formats, GCHandleType.Pinned);

            DioDataFormat dataFormat = new DioDataFormat
            {
                dwSize = (uint)Marshal.SizeOf(typeof(DioDataFormat)),
                dwObjSize = (uint)Marshal.SizeOf(typeof(DioObjectDataFormat)),
                dwFlags = DidfAbsAxis,
                dwDataSize = (uint)Marshal.SizeOf(typeof(DirectInputJoyState)),
                dwNumObjs = (uint)formats.Length,
                rgodf = formatsHandle.AddrOfPinnedObject()
            };

            int hr = device.SetDataFormat(ref dataFormat);
            if (hr != 0)
            {
                error = "DirectInput SetDataFormat failed: 0x" + hr.ToString("X8");
                return false;
            }

            if (!TrySetDirectInputCooperativeLevel(device, out string cooperativeError))
            {
                error = cooperativeError;
                return false;
            }

            device.Acquire();
            return true;
        }
        catch (Exception ex)
        {
            error = "DirectInput device configure failed: " + ex.Message;
            return false;
        }
        finally
        {
            if (formatsHandle.IsAllocated)
                formatsHandle.Free();
        }
    }

    private static bool TrySetDirectInputCooperativeLevel(IDirectInputDevice8W device, out string error)
    {
        error = string.Empty;

        List<IntPtr> handles = [];

        AddHandleIfUsable(handles, TryGetCurrentProcessMainWindowHandle());
        AddHandleIfUsable(handles, GetActiveWindow());
        AddHandleIfUsable(handles, GetForegroundWindow());
        AddHandleIfUsable(handles, GetDesktopWindow(), allowForeignWindow: true);

        // Always keep a null-handle fallback at the end.
        AddHandleIfUsable(handles, IntPtr.Zero, allowForeignWindow: true);

        uint[] flagCandidates =
        [
            DisclBackground | DisclNonExclusive,
            DisclForeground | DisclNonExclusive
        ];

        foreach (IntPtr handle in handles)
        {
            foreach (uint flags in flagCandidates)
            {
                int hr = device.SetCooperativeLevel(handle, flags);
                if (hr == 0)
                    return true;
            }
        }

        error = "DirectInput SetCooperativeLevel failed for all handle/flag combinations.";
        return false;
    }

    private static IntPtr TryGetCurrentProcessMainWindowHandle()
    {
        try
        {
            return Process.GetCurrentProcess().MainWindowHandle;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    private static void AddHandleIfUsable(List<IntPtr> handles, IntPtr handle, bool allowForeignWindow = false)
    {
        if (handles.Contains(handle))
            return;

        if (handle == IntPtr.Zero)
        {
            handles.Add(handle);
            return;
        }

        if (!allowForeignWindow && !IsWindowOwnedByCurrentProcess(handle))
            return;

        handles.Add(handle);
    }

    private static bool IsWindowOwnedByCurrentProcess(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
            return false;

        _ = GetWindowThreadProcessId(handle, out uint processId);
        if (processId == 0)
            return false;

        return processId == (uint)Environment.ProcessId;
    }

    private static bool TryReadDirectInputState(IDirectInputDevice8W device, out DirectInputJoyState state)
    {
        state = CreateEmptyDirectInputState();

        int size = Marshal.SizeOf(typeof(DirectInputJoyState));
        IntPtr ptr = Marshal.AllocHGlobal(size);
        try
        {
            int pollHr = device.Poll();
            _ = pollHr;

            int hr = device.GetDeviceState(size, ptr);
            if (hr != 0)
            {
                device.Acquire();
                device.Poll();
                hr = device.GetDeviceState(size, ptr);
                if (hr != 0)
                    return false;
            }

            state = Marshal.PtrToStructure<DirectInputJoyState>(ptr);
            NormalizeDirectInputState(ref state);
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    private static void NormalizeDirectInputState(ref DirectInputJoyState state)
    {
        if (state.Slider == null || state.Slider.Length != 2)
            state.Slider = new int[2];
        if (state.Buttons == null || state.Buttons.Length != 128)
            state.Buttons = new byte[128];
        if (state.Pov == null || state.Pov.Length != 4)
            state.Pov = new uint[4];
        if (state.VSlider == null || state.VSlider.Length != 2)
            state.VSlider = new int[2];
        if (state.ASlider == null || state.ASlider.Length != 2)
            state.ASlider = new int[2];
        if (state.FSlider == null || state.FSlider.Length != 2)
            state.FSlider = new int[2];
    }

    private static DirectInputJoyState CreateEmptyDirectInputState()
    {
        return new DirectInputJoyState
        {
            Slider = new int[2],
            Pov = new uint[4],
            Buttons = new byte[128],
            VSlider = new int[2],
            ASlider = new int[2],
            FSlider = new int[2]
        };
    }

    private static Dictionary<int, bool> CaptureKeyboardStates()
    {
        Dictionary<int, bool> states = new();
        foreach (KeyValuePair<int, string> entry in KeyboardVirtualKeyMap)
            states[entry.Key] = IsVirtualKeyDown(entry.Key);
        return states;
    }

    private static bool TryDetectKeyboardEdge(
        Dictionary<int, bool> previousStates,
        Dictionary<int, bool> currentStates,
        out string input)
    {
        input = string.Empty;
        foreach (KeyValuePair<int, string> entry in KeyboardVirtualKeyMap)
        {
            bool hadPrevious = previousStates.TryGetValue(entry.Key, out bool previousDown) && previousDown;
            bool isCurrent = currentStates.TryGetValue(entry.Key, out bool currentDown) && currentDown;
            if (isCurrent && !hadPrevious)
            {
                input = entry.Value;
                return true;
            }
        }

        return false;
    }

    private static bool IsVirtualKeyDown(int virtualKey)
    {
        short state = GetAsyncKeyState(virtualKey);
        return (state & 0x8000) != 0;
    }

    private static List<KeyValuePair<int, string>> BuildKeyboardVirtualKeyMap()
    {
        List<KeyValuePair<int, string>> map = [];

        // Letters and number row.
        for (int vk = 0x41; vk <= 0x5A; vk++)
            map.Add(new KeyValuePair<int, string>(vk, ((char)vk).ToString()));
        for (int vk = 0x30; vk <= 0x39; vk++)
            map.Add(new KeyValuePair<int, string>(vk, "D" + (vk - 0x30).ToString(CultureInfo.InvariantCulture)));

        // Function keys.
        for (int vk = 0x70; vk <= 0x87; vk++)
            map.Add(new KeyValuePair<int, string>(vk, "F" + (vk - 0x6F).ToString(CultureInfo.InvariantCulture)));

        // Modifiers and common controls.
        map.Add(new KeyValuePair<int, string>(0xA0, "LShift"));
        map.Add(new KeyValuePair<int, string>(0xA1, "RShift"));
        map.Add(new KeyValuePair<int, string>(0xA2, "LCtrl"));
        map.Add(new KeyValuePair<int, string>(0xA3, "RCtrl"));
        map.Add(new KeyValuePair<int, string>(0xA4, "LAlt"));
        map.Add(new KeyValuePair<int, string>(0xA5, "RAlt"));
        map.Add(new KeyValuePair<int, string>(0x20, "Space"));
        map.Add(new KeyValuePair<int, string>(0x09, "Tab"));
        map.Add(new KeyValuePair<int, string>(0x0D, "Enter"));
        map.Add(new KeyValuePair<int, string>(0x08, "Backspace"));
        map.Add(new KeyValuePair<int, string>(0x1B, "Escape"));
        map.Add(new KeyValuePair<int, string>(0x14, "CapsLock"));

        // Navigation.
        map.Add(new KeyValuePair<int, string>(0x25, "Left"));
        map.Add(new KeyValuePair<int, string>(0x26, "Up"));
        map.Add(new KeyValuePair<int, string>(0x27, "Right"));
        map.Add(new KeyValuePair<int, string>(0x28, "Down"));
        map.Add(new KeyValuePair<int, string>(0x21, "PageUp"));
        map.Add(new KeyValuePair<int, string>(0x22, "PageDown"));
        map.Add(new KeyValuePair<int, string>(0x23, "End"));
        map.Add(new KeyValuePair<int, string>(0x24, "Home"));
        map.Add(new KeyValuePair<int, string>(0x2D, "Insert"));
        map.Add(new KeyValuePair<int, string>(0x2E, "Delete"));

        // Numpad.
        for (int vk = 0x60; vk <= 0x69; vk++)
            map.Add(new KeyValuePair<int, string>(vk, "NumPad" + (vk - 0x60).ToString(CultureInfo.InvariantCulture)));
        map.Add(new KeyValuePair<int, string>(0x6A, "Multiply"));
        map.Add(new KeyValuePair<int, string>(0x6B, "Add"));
        map.Add(new KeyValuePair<int, string>(0x6D, "Subtract"));
        map.Add(new KeyValuePair<int, string>(0x6E, "Decimal"));
        map.Add(new KeyValuePair<int, string>(0x6F, "Divide"));

        // Punctuation keys.
        map.Add(new KeyValuePair<int, string>(0xBA, "Semicolon"));
        map.Add(new KeyValuePair<int, string>(0xBB, "Equals"));
        map.Add(new KeyValuePair<int, string>(0xBC, "Comma"));
        map.Add(new KeyValuePair<int, string>(0xBD, "Minus"));
        map.Add(new KeyValuePair<int, string>(0xBE, "Period"));
        map.Add(new KeyValuePair<int, string>(0xBF, "Slash"));
        map.Add(new KeyValuePair<int, string>(0xC0, "Grave"));
        map.Add(new KeyValuePair<int, string>(0xDB, "LBracket"));
        map.Add(new KeyValuePair<int, string>(0xDC, "Backslash"));
        map.Add(new KeyValuePair<int, string>(0xDD, "RBracket"));
        map.Add(new KeyValuePair<int, string>(0xDE, "Apostrophe"));

        return map;
    }

    private static List<WinMmDevice> EnumerateWinMmDevices()
    {
        List<WinMmDevice> output = [];
        uint deviceSlots = joyGetNumDevs();
        uint capsSize = (uint)Marshal.SizeOf(typeof(JoyCaps));
        for (uint id = 0; id < deviceSlots; id++)
        {
            JoyCaps caps = new JoyCaps();
            int result = joyGetDevCaps(id, ref caps, capsSize);
            if (result != JoyErrNoError)
                continue;

            if (!TryReadState(id, out JoyInfoEx state))
                continue;

            string name = (caps.ProductName ?? string.Empty).Trim().Trim('\0');
            if (name.Length == 0)
                name = "Joystick " + id.ToString(CultureInfo.InvariantCulture);

            output.Add(new WinMmDevice(id, name, caps));
        }
        return output;
    }

    private static bool TryReadState(uint deviceId, out JoyInfoEx state)
    {
        state = new JoyInfoEx
        {
            Size = (uint)Marshal.SizeOf(typeof(JoyInfoEx)),
            Flags = JoyReturnAll | JoyReturnPovCts | JoyReturnButtons
        };

        int result = joyGetPosEx(deviceId, ref state);
        return result == JoyErrNoError;
    }

    private static bool TryDetectButtonOrPov(DirectInputJoyState lastState, DirectInputJoyState currentState, out string input)
    {
        input = string.Empty;

        byte[] previousButtons = lastState.Buttons ?? Array.Empty<byte>();
        byte[] currentButtons = currentState.Buttons ?? Array.Empty<byte>();
        string? buttonEdge = DetectDirectInputButtonEdge(previousButtons, currentButtons);
        if (!string.IsNullOrWhiteSpace(buttonEdge))
        {
            input = buttonEdge;
            return true;
        }

        uint[] previousPov = lastState.Pov ?? Array.Empty<uint>();
        uint[] currentPov = currentState.Pov ?? Array.Empty<uint>();
        int povCount = Math.Min(previousPov.Length, currentPov.Length);
        for (int i = 0; i < povCount; i++)
        {
            uint prev = previousPov[i];
            uint cur = currentPov[i];
            if (cur == prev || !TryNormalizePovAngle(cur, out int normalizedPov))
                continue;

            string? suffix = PovSuffix(normalizedPov);
            if (suffix == null)
                continue;

            input = "JOY_BTN_POV" + (i + 1).ToString(CultureInfo.InvariantCulture) + "_" + suffix;
            return true;
        }

        return false;
    }

    internal static string? DetectDirectInputButtonEdge(IReadOnlyList<byte> previousButtons, IReadOnlyList<byte> currentButtons)
    {
        if (previousButtons == null || currentButtons == null)
            return null;

        int buttonCount = Math.Min(previousButtons.Count, currentButtons.Count);
        for (int i = 0; i < buttonCount; i++)
        {
            bool wasDown = previousButtons[i] != 0;
            bool isDown = currentButtons[i] != 0;
            if (isDown && !wasDown)
                return "JOY_BTN" + (i + 1).ToString(CultureInfo.InvariantCulture);
        }

        return null;
    }

    private static bool TryDetectAxis(
        JoyInfoEx lastState,
        JoyInfoEx currentState,
        JoyCaps caps,
        int thresholdPercent,
        out string input)
    {
        input = string.Empty;
        if (Crossed(lastState.XPos, currentState.XPos, caps.XMin, caps.XMax, thresholdPercent))
        {
            input = "JOY_X";
            return true;
        }
        if (Crossed(lastState.YPos, currentState.YPos, caps.YMin, caps.YMax, thresholdPercent))
        {
            input = "JOY_Y";
            return true;
        }
        if (Crossed(lastState.ZPos, currentState.ZPos, caps.ZMin, caps.ZMax, thresholdPercent))
        {
            input = "JOY_Z";
            return true;
        }
        if (Crossed(lastState.RPos, currentState.RPos, caps.RMin, caps.RMax, thresholdPercent))
        {
            input = "JOY_RX";
            return true;
        }
        if (Crossed(lastState.UPos, currentState.UPos, caps.UMin, caps.UMax, thresholdPercent))
        {
            input = "JOY_RY";
            return true;
        }
        if (Crossed(lastState.VPos, currentState.VPos, caps.VMin, caps.VMax, thresholdPercent))
        {
            input = "JOY_RZ";
            return true;
        }

        return false;
    }

    private static bool Crossed(uint previous, uint current, uint min, uint max, int thresholdPercent)
    {
        if (max <= min)
            return false;
        double range = max - min;
        if (range < 1)
            return false;

        double threshold = range * thresholdPercent / 100.0;
        double diff = previous > current ? previous - current : current - previous;
        return diff >= threshold;
    }

    private static string? PovSuffix(int povValue)
    {
        return povValue switch
        {
            0 => "U",
            4500 => "UR",
            9000 => "R",
            13500 => "DR",
            18000 => "D",
            22500 => "DL",
            27000 => "L",
            31500 => "UL",
            _ => null
        };
    }

    private static bool TryNormalizePovAngle(uint value, out int normalized)
    {
        normalized = 0;
        if (value == 0xFFFFFFFF || value == 0xFFFF || value > 35999)
            return false;

        int raw = (int)value;
        int snapped = ((raw + 2250) / 4500) * 4500;
        if (snapped >= 36000)
            snapped = 0;
        normalized = snapped;
        return true;
    }

    private static string ToDirectInputInstanceString(string? instanceName, Guid instanceGuid)
    {
        string safeName = (instanceName ?? string.Empty)
            .Replace("/", "_")
            .Replace("\"", "_")
            .Trim();
        if (safeName.Length == 0)
            return string.Empty;

        string id = instanceGuid.ToString();
        string[] idParts = id.Split('-');
        if (idParts.Length > 2)
        {
            id = idParts[0].ToUpperInvariant() + "-" +
                 idParts[1].ToUpperInvariant() + "-" +
                 idParts[2].ToLowerInvariant();
            for (int i = 3; i < idParts.Length; i++)
                id += "-" + idParts[i].ToUpperInvariant();
        }

        return safeName + " {" + id + "}";
    }

    private static string ResolveBestDeviceName(string winMmName, List<string> directInputDevices)
    {
        if (directInputDevices == null || directInputDevices.Count == 0)
            return winMmName;

        string normalizedWinMm = NormalizeDeviceName(winMmName);
        if (normalizedWinMm.Length == 0)
            return winMmName;

        List<string> exact = [];
        List<string> contains = [];

        foreach (string candidate in directInputDevices)
        {
            string candidateName = ExtractDeviceName(candidate);
            string normalizedCandidate = NormalizeDeviceName(candidateName);
            if (normalizedCandidate.Length == 0)
                continue;

            if (string.Equals(normalizedWinMm, normalizedCandidate, StringComparison.OrdinalIgnoreCase))
                exact.Add(candidate);
            else if (normalizedCandidate.Contains(normalizedWinMm, StringComparison.OrdinalIgnoreCase) ||
                     normalizedWinMm.Contains(normalizedCandidate, StringComparison.OrdinalIgnoreCase))
                contains.Add(candidate);
        }

        if (exact.Count == 1)
            return exact[0];
        if (contains.Count == 1)
            return contains[0];

        if (IsGenericWinMmName(winMmName))
        {
            List<string> specific = directInputDevices
                .Where(candidate => !IsGenericDirectInputName(ExtractDeviceName(candidate)))
                .ToList();
            if (specific.Count == 1)
                return specific[0];
            if (specific.Count > 0)
                return specific[0];
            if (directInputDevices.Count > 0)
                return directInputDevices[0];
        }

        return winMmName;
    }

    private static string ExtractDeviceName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        string trimmed = value.Trim();
        int braceIndex = trimmed.LastIndexOf(" {", StringComparison.Ordinal);
        if (braceIndex > 0 && trimmed.EndsWith("}", StringComparison.Ordinal))
            return trimmed[..braceIndex].Trim();
        return trimmed;
    }

    private static string NormalizeDeviceName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        return value.Trim()
            .Replace("/", " ")
            .Replace("\\", " ")
            .Replace("-", " ")
            .Replace("_", " ")
            .Replace("  ", " ")
            .ToUpperInvariant();
    }

    private static bool IsGenericWinMmName(string value)
    {
        string normalized = NormalizeDeviceName(value);
        if (normalized.Length == 0)
            return true;
        return normalized.Contains("MICROSOFT PC JOYSTICK DRIVER", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("HID COMPLIANT GAME CONTROLLER", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("JOYSTICK", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGenericDirectInputName(string value)
    {
        string normalized = NormalizeDeviceName(value);
        if (normalized.Length == 0)
            return true;
        return normalized.Contains("HID COMPLIANT GAME CONTROLLER", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("JOYSTICK", StringComparison.OrdinalIgnoreCase);
    }

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern uint joyGetNumDevs();

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern int joyGetDevCaps(uint uJoyID, ref JoyCaps pjc, uint cbjc);

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern int joyGetPosEx(uint uJoyID, ref JoyInfoEx pji);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetActiveWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("dinput8.dll", CharSet = CharSet.Unicode, EntryPoint = "DirectInput8Create", PreserveSig = true)]
    private static extern int DirectInput8Create(
        IntPtr hinst,
        uint dwVersion,
        ref Guid riidltf,
        [MarshalAs(UnmanagedType.Interface)] out IDirectInput8W ppvOut,
        IntPtr punkOuter);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumDevicesCallback(IntPtr lpddi, IntPtr pvRef);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DideviceInstanceW
    {
        public int dwSize;
        public Guid guidInstance;
        public Guid guidProduct;
        public int dwDevType;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string tszInstanceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string tszProductName;
        public Guid guidFFDriver;
        public ushort wUsagePage;
        public ushort wUsage;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DioObjectDataFormat
    {
        public IntPtr pguid;
        public uint dwOfs;
        public uint dwType;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DioDataFormat
    {
        public uint dwSize;
        public uint dwObjSize;
        public uint dwFlags;
        public uint dwDataSize;
        public uint dwNumObjs;
        public IntPtr rgodf;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DirectInputJoyState
    {
        public int X;
        public int Y;
        public int Z;
        public int Rx;
        public int Ry;
        public int Rz;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)]
        public int[] Slider;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
        public uint[] Pov;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)]
        public byte[] Buttons;
        public int VX;
        public int VY;
        public int VZ;
        public int VRx;
        public int VRy;
        public int VRz;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)]
        public int[] VSlider;
        public int AX;
        public int AY;
        public int AZ;
        public int ARx;
        public int ARy;
        public int ARz;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)]
        public int[] ASlider;
        public int FX;
        public int FY;
        public int FZ;
        public int FRx;
        public int FRy;
        public int FRz;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)]
        public int[] FSlider;
    }

    private sealed class DirectInputEnumerationState
    {
        public List<DideviceInstanceW> Instances { get; } = [];
    }

    [ComImport, Guid("BF798031-483A-4DA2-AA99-5D64ED369700"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirectInput8W
    {
        [PreserveSig]
        int CreateDevice(ref Guid rguid, [MarshalAs(UnmanagedType.Interface)] out IDirectInputDevice8W lplpDirectInputDevice, IntPtr pUnkOuter);

        [PreserveSig]
        int EnumDevices(uint dwDevType, IntPtr lpCallback, IntPtr pvRef, uint dwFlags);

        [PreserveSig]
        int GetDeviceStatus(ref Guid rguidInstance);

        [PreserveSig]
        int RunControlPanel(IntPtr hwndOwner, uint dwFlags);

        [PreserveSig]
        int Initialize(IntPtr hinst, uint dwVersion);

        [PreserveSig]
        int FindDevice(ref Guid rguidClass, [MarshalAs(UnmanagedType.LPWStr)] string ptszName, out Guid pguidInstance);

        [PreserveSig]
        int EnumDevicesBySemantics(
            [MarshalAs(UnmanagedType.LPWStr)] string ptszUserName,
            IntPtr lpdiActionFormat,
            IntPtr lpCallback,
            IntPtr pvRef,
            uint dwFlags);

        [PreserveSig]
        int ConfigureDevices(
            IntPtr lpdiCallback,
            IntPtr lpdiCDParams,
            uint dwFlags,
            IntPtr pvRefData);
    }

    [ComImport, Guid("54D41080-DC15-4833-A41B-748F73A38179"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirectInputDevice8W
    {
        [PreserveSig]
        int GetCapabilities(IntPtr lpDIDevCaps);

        [PreserveSig]
        int EnumObjects(IntPtr lpCallback, IntPtr pvRef, uint dwFlags);

        [PreserveSig]
        int GetProperty(ref Guid rguidProp, IntPtr pdiph);

        [PreserveSig]
        int SetProperty(ref Guid rguidProp, IntPtr pdiph);

        [PreserveSig]
        int Acquire();

        [PreserveSig]
        int Unacquire();

        [PreserveSig]
        int GetDeviceState(int cbData, IntPtr lpvData);

        [PreserveSig]
        int GetDeviceData(int cbObjectData, IntPtr rgdod, ref int pdwInOut, uint dwFlags);

        [PreserveSig]
        int SetDataFormat(ref DioDataFormat lpdf);

        [PreserveSig]
        int SetEventNotification(IntPtr hEvent);

        [PreserveSig]
        int SetCooperativeLevel(IntPtr hwnd, uint dwFlags);

        [PreserveSig]
        int GetObjectInfo(IntPtr pdidoi, uint dwObj, uint dwHow);

        [PreserveSig]
        int GetDeviceInfo(IntPtr pdidi);

        [PreserveSig]
        int RunControlPanel(IntPtr hwndOwner, uint dwFlags);

        [PreserveSig]
        int Initialize(IntPtr hinst, uint dwVersion, ref Guid rguid);

        [PreserveSig]
        int CreateEffect(ref Guid rguid, IntPtr lpeff, out IntPtr ppdeff, IntPtr pUnkOuter);

        [PreserveSig]
        int EnumEffects(IntPtr lpCallback, IntPtr pvRef, uint dwEffType);

        [PreserveSig]
        int GetEffectInfo(IntPtr pdei, ref Guid rguid);

        [PreserveSig]
        int GetForceFeedbackState(out uint pdwOut);

        [PreserveSig]
        int SendForceFeedbackCommand(uint dwFlags);

        [PreserveSig]
        int EnumCreatedEffectObjects(IntPtr lpCallback, IntPtr pvRef, uint fl);

        [PreserveSig]
        int Escape(IntPtr pesc);

        [PreserveSig]
        int Poll();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct JoyCaps
    {
        public ushort ManufacturerId;
        public ushort ProductId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string ProductName;
        public uint XMin;
        public uint XMax;
        public uint YMin;
        public uint YMax;
        public uint ZMin;
        public uint ZMax;
        public uint NumButtons;
        public uint PeriodMin;
        public uint PeriodMax;
        public uint RMin;
        public uint RMax;
        public uint UMin;
        public uint UMax;
        public uint VMin;
        public uint VMax;
        public uint Caps;
        public uint MaxAxes;
        public uint NumAxes;
        public uint MaxButtons;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string RegistryKey;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string OemVxd;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JoyInfoEx
    {
        public uint Size;
        public uint Flags;
        public uint XPos;
        public uint YPos;
        public uint ZPos;
        public uint RPos;
        public uint UPos;
        public uint VPos;
        public uint Buttons;
        public uint ButtonNumber;
        public uint Pov;
        public uint Reserved1;
        public uint Reserved2;
    }

    private sealed class DirectInputPollingDevice : IDisposable
    {
        public DirectInputPollingDevice(string displayName, IDirectInputDevice8W device, DirectInputJoyState lastState)
        {
            DisplayName = displayName;
            Device = device;
            LastState = lastState;
        }

        public string DisplayName { get; }
        public IDirectInputDevice8W Device { get; }
        public DirectInputJoyState LastState { get; set; }
        public double[]? LastAxes { get; set; }
        // Record: the axis values when recording armed.
        public int[]? BaselineAxes { get; set; }
        // Record: the axes changed at least once since the device was opened (its state is a real report).
        public bool AxesReported { get; set; }
        // Record: which button or hat counts (transitions only, never one held at the start).
        public RecordButtonTracker? Buttons { get; set; }
        // Monitor: the device sent a report (its state is real, not DirectInput's empty defaults).
        public bool Reported { get; set; }

        public void Dispose()
        {
            try
            {
                Device.Unacquire();
            }
            catch
            {
                // Ignore release cleanup errors.
            }

            if (Marshal.IsComObject(Device))
                Marshal.ReleaseComObject(Device);
        }
    }

    private sealed class WinMmDevice
    {
        public WinMmDevice(uint id, string productName, JoyCaps caps)
        {
            Id = id;
            ProductName = productName;
            Caps = caps;
        }

        public uint Id { get; }
        public string ProductName { get; }
        public JoyCaps Caps { get; }
    }
}
