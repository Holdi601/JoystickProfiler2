using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using JoyPro.Engine.VJoy;

namespace JoyPro.VJoy;

/// <summary>Where the vJoy positions go: vJoyInterface.dll, or nowhere (dry: tests and the live view without vJoy).</summary>
internal interface IVJoySink : IDisposable
{
    string Name { get; }

    /// <summary>Takes the vJoy devices; false with a reason when one cannot be used (missing, used by another program).</summary>
    bool Open(IReadOnlyList<int> deviceIds, out string error);

    int AxisMin { get; }

    int AxisMax { get; }

    /// <summary>Devices with 4-way hats only (vJoyConfig -s).</summary>
    IReadOnlyDictionary<int, bool> DiscreteHats { get; }

    bool Update(int deviceId, ref VJoyPosition position);
}

internal sealed class DrySink : IVJoySink
{
    public string Name => "dry";
    public int AxisMin => 0;
    public int AxisMax => 32767;
    public IReadOnlyDictionary<int, bool> DiscreteHats { get; } = new Dictionary<int, bool>();
    public long Updates { get; private set; }

    public bool Open(IReadOnlyList<int> deviceIds, out string error)
    {
        error = "";
        return true;
    }

    public bool Update(int deviceId, ref VJoyPosition position)
    {
        Updates++;
        return true;
    }

    public void Dispose()
    {
    }
}

/// <summary>The real vJoy devices through vJoyInterface.dll (acquired for the run, neutral and released at the end).</summary>
internal sealed class VJoyInterfaceSink : IVJoySink
{
    private readonly List<uint> _acquired = [];
    private readonly Dictionary<int, bool> _discrete = [];

    public string Name => "vjoy";
    public int AxisMin { get; private set; }
    public int AxisMax { get; private set; } = 32767;
    public IReadOnlyDictionary<int, bool> DiscreteHats => _discrete;

    public bool Open(IReadOnlyList<int> deviceIds, out string error)
    {
        error = "";
        try
        {
            if (!VJoyNative.vJoyEnabled())
            {
                error = "vJoy is not enabled (driver missing or no device configured).";
                return false;
            }

            var first = true;
            foreach (var id in deviceIds)
            {
                var rid = (uint)id;
                var status = VJoyNative.GetVJDStatus(rid);
                if (status is not (VJoyNative.VjdStat.Free or VJoyNative.VjdStat.Own))
                {
                    error = status == VJoyNative.VjdStat.Busy
                        ? "vJoy device " + id + " is used by another program (a feeder such as Joystick Gremlin or JoyPro's test kit)."
                        : "vJoy device " + id + " cannot be used: " + status + " (configure it on JoyPro's virtual joystick page).";
                    Dispose();
                    return false;
                }

                if (!VJoyNative.AcquireVJD(rid))
                {
                    error = "vJoy device " + id + " could not be acquired.";
                    Dispose();
                    return false;
                }

                _acquired.Add(rid);
                _discrete[id] = VJoyNative.GetVJDContPovNumber(rid) == 0 && VJoyNative.GetVJDDiscPovNumber(rid) > 0;
                if (first)
                {
                    long min = 0;
                    long max = 0;
                    VJoyNative.GetVJDAxisMin(rid, VJoyNative.HidUsageX, ref min);
                    VJoyNative.GetVJDAxisMax(rid, VJoyNative.HidUsageX, ref max);
                    if (max > min)
                    {
                        AxisMin = (int)min;
                        AxisMax = (int)max;
                    }

                    first = false;
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            error = "vJoyInterface.dll could not be loaded: " + ex.Message;
            return false;
        }
    }

    public bool Update(int deviceId, ref VJoyPosition position) => VJoyNative.UpdateVJD((uint)deviceId, ref position);

    public void Dispose()
    {
        foreach (var rid in _acquired)
        {
            try
            {
                VJoyNative.RelinquishVJD(rid);
            }
            catch (Exception)
            {
            }
        }

        _acquired.Clear();
    }
}

/// <summary>Where the virtual Xbox pad's report goes: ViGEmBus, or nowhere (dry).</summary>
internal interface IXboxSink : IDisposable
{
    string Name { get; }

    bool Open(out string error);

    void Update(ref XboxReport report);
}

internal sealed class DryXboxSink : IXboxSink
{
    public string Name => "dry";
    public long Updates { get; private set; }

    public bool Open(out string error)
    {
        error = "";
        return true;
    }

    public void Update(ref XboxReport report) => Updates++;

    public void Dispose()
    {
    }
}

/// <summary>A virtual Xbox 360 pad through ViGEmBus (Nefarius.ViGEm.Client, MIT). Connected for the run, neutral and
/// disconnected at the end. Needs the ViGEmBus driver (installed by the user, or by the GUI's Components card on the
/// user's click; docs/components.md). This process never installs anything.</summary>
internal sealed class ViGEmXboxSink : IXboxSink
{
    private Nefarius.ViGEm.Client.ViGEmClient? _client;
    private Nefarius.ViGEm.Client.Targets.IXbox360Controller? _pad;

    public string Name => "vigem";

    public bool Open(out string error)
    {
        error = "";
        try
        {
            _client = new Nefarius.ViGEm.Client.ViGEmClient();
            _pad = _client.CreateXbox360Controller();
            _pad.AutoSubmitReport = false;
            _pad.Connect();
            return true;
        }
        catch (Exception ex)
        {
            error = "The virtual Xbox pad could not be created (is the ViGEmBus driver installed? https://github.com/nefarius/ViGEmBus/releases): " + ex.Message;
            Dispose();
            return false;
        }
    }

    public void Update(ref XboxReport report)
    {
        var pad = _pad;
        if (pad is null)
            return;
        pad.SetButtonsFull(report.Buttons);
        pad.LeftTrigger = report.LeftTrigger;
        pad.RightTrigger = report.RightTrigger;
        pad.LeftThumbX = report.LeftX;
        pad.LeftThumbY = report.LeftY;
        pad.RightThumbX = report.RightX;
        pad.RightThumbY = report.RightY;
        pad.SubmitReport();
    }

    public void Dispose()
    {
        try
        {
            if (_pad is not null)
            {
                var neutral = default(XboxReport);
                Update(ref neutral);
                _pad.Disconnect();
            }
        }
        catch (Exception)
        {
        }

        _pad = null;
        _client?.Dispose();
        _client = null;
    }
}

/// <summary>Keys of virtual functions that the user switched to keyboard output, nowhere (dry runs, the Test button).</summary>
internal sealed class DryKeySink : IVfKeySink
{
    public long Events { get; private set; }

    public void Key(int scan, bool down) => Events++;
}

/// <summary>
/// Keys of virtual functions with "Keyboard output" on, through SendInput with scan codes (games read scan codes;
/// + 0x100 = extended key). Only with a real sink (--sink vjoy / --xbox-sink vigem); one preallocated INPUT, no
/// allocation per key.
/// </summary>
internal sealed class SendInputKeySink : IVfKeySink
{
    private const uint InputKeyboard = 1;
    private const uint KeyEventExtended = 0x0001;
    private const uint KeyEventUp = 0x0002;
    private const uint KeyEventScanCode = 0x0008;
    private KeyboardInput _input;

    public long Sent { get; private set; }

    public void Key(int scan, bool down)
    {
        _input = default;
        _input.Type = InputKeyboard;
        _input.Scan = (ushort)(scan & 0xFF);
        _input.Flags = KeyEventScanCode | (down ? 0 : KeyEventUp) | ((scan & 0x100) != 0 ? KeyEventExtended : 0);
        if (SendInput(1, ref _input, Marshal.SizeOf<KeyboardInput>()) == 1)
            Sent++;
    }

    // INPUT with KEYBDINPUT on x64 (40 bytes: type, padding, union of 32 bytes).
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct KeyboardInput
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public ushort Vk;
        [FieldOffset(10)] public ushort Scan;
        [FieldOffset(12)] public uint Flags;
        [FieldOffset(16)] public uint Time;
        [FieldOffset(24)] public IntPtr Extra;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, ref KeyboardInput input, int size);
}

/// <summary>One physical device read through DirectInput (non-exclusive, background: the device stays usable by everyone).</summary>
internal sealed class DirectInputSource : IDisposable
{
    private IntPtr _current;
    private IntPtr _previous;
    private Native.IDirectInputDevice8W? _device;
    private bool _forceNext;

    public DirectInputSource(int index, ForwarderSource source)
    {
        Index = index;
        Source = source;
        Event = Native.CreateEventW(IntPtr.Zero, false, false, IntPtr.Zero);
        _current = Marshal.AllocHGlobal(ForwardingTable.StateSize);
        _previous = Marshal.AllocHGlobal(ForwardingTable.StateSize);
        Clear(_current);
        Clear(_previous);
    }

    public int Index { get; }
    public ForwarderSource Source { get; }
    public IntPtr Event { get; }
    public bool Connected { get; private set; }
    public long NextRetry { get; set; }
    public string LastError { get; private set; } = "";

    public unsafe ReadOnlySpan<byte> State => new((void*)_current, ForwardingTable.StateSize);

    public bool Open(Native.IDirectInput8W directInput, IntPtr window, IntPtr dataFormat, int axisMin, int axisMax)
    {
        CloseDevice();
        var guid = Source.InstanceGuid;
        if (guid == Guid.Empty || directInput.CreateDevice(ref guid, out var device, IntPtr.Zero) < 0 || device is null)
        {
            LastError = "not connected";
            return false;
        }

        _device = device;
        var hr = device.SetDataFormat(dataFormat);
        if (hr >= 0)
            hr = device.SetCooperativeLevel(window, Native.DisclNonExclusive | Native.DisclBackground);
        if (hr < 0)
        {
            LastError = "DirectInput setup failed: 0x" + hr.ToString("X8", CultureInfo.InvariantCulture);
            CloseDevice();
            return false;
        }

        // DIPROPRANGE for every axis of the device: the values come in vJoy's range, so they go over unchanged.
        var range = Marshal.AllocHGlobal(24);
        try
        {
            Marshal.WriteInt32(range, 0, 24);   // dwSize
            Marshal.WriteInt32(range, 4, 16);   // dwHeaderSize
            Marshal.WriteInt32(range, 8, 0);    // dwObj
            Marshal.WriteInt32(range, 12, 0);   // dwHow = DIPH_DEVICE
            Marshal.WriteInt32(range, 16, axisMin);
            Marshal.WriteInt32(range, 20, axisMax);
            device.SetProperty(new IntPtr(Native.DiPropRange), range);
        }
        finally
        {
            Marshal.FreeHGlobal(range);
        }

        device.SetEventNotification(Event);
        hr = device.Acquire();
        Connected = hr >= 0;
        _forceNext = true;
        LastError = Connected ? "" : "Acquire failed: 0x" + hr.ToString("X8", CultureInfo.InvariantCulture);
        return Connected;
    }

    /// <summary>Reads the state: 1 = changed, 0 = unchanged, &lt; 0 = DirectInput error (device lost).</summary>
    public unsafe int Read()
    {
        var device = _device;
        if (device is null)
            return -1;
        device.Poll();
        var hr = device.GetDeviceState(ForwardingTable.StateSize, _current);
        if (hr == Native.DiErrInputLost || hr == Native.DiErrNotAcquired)
        {
            if (device.Acquire() >= 0)
            {
                device.Poll();
                hr = device.GetDeviceState(ForwardingTable.StateSize, _current);
            }
        }

        if (hr < 0)
        {
            Connected = false;
            LastError = "lost (0x" + ((uint)hr).ToString("X8", CultureInfo.InvariantCulture) + ")";
            return hr;
        }

        var same = new ReadOnlySpan<byte>((void*)_current, ForwardingTable.StateSize)
            .SequenceEqual(new ReadOnlySpan<byte>((void*)_previous, ForwardingTable.StateSize));
        if (same && !_forceNext)
            return 0;
        _forceNext = false;
        Buffer.MemoryCopy((void*)_current, (void*)_previous, ForwardingTable.StateSize, ForwardingTable.StateSize);
        return 1;
    }

    public void CloseDevice()
    {
        Connected = false;
        if (_device is null)
            return;
        try
        {
            _device.Unacquire();
            _device.SetEventNotification(IntPtr.Zero);
        }
        catch (Exception)
        {
        }

        try
        {
            Marshal.ReleaseComObject(_device);
        }
        catch (Exception)
        {
        }

        _device = null;
    }

    public void Dispose()
    {
        CloseDevice();
        if (Event != IntPtr.Zero)
            Native.CloseHandle(Event);
        if (_current != IntPtr.Zero)
            Marshal.FreeHGlobal(_current);
        if (_previous != IntPtr.Zero)
            Marshal.FreeHGlobal(_previous);
        _current = _previous = IntPtr.Zero;
    }

    private static unsafe void Clear(IntPtr state)
    {
        var span = new Span<byte>((void*)state, ForwardingTable.StateSize);
        span.Clear();
        // Hats centred.
        for (var i = 0; i < 4; i++)
            Marshal.WriteInt32(state, ForwardingTable.HatOffset + i * 4, -1);
    }
}

/// <summary>
/// The forwarding loop: reads every mapped physical device and writes the vJoy devices.
/// </summary>
/// <remarks>
/// - Wakes on DirectInput's change events (a button press goes out at once) and on a high-resolution timer at
///   <c>rateHz</c> (at least the fastest display's refresh rate, 60 Hz minimum) as the poll for devices without
///   events. Event wake-ups closer together than half a timer period wait for the next tick, so a device that reports
///   1000 times a second (or a noisy force feedback stick) costs at most two passes per period.
/// - A vJoy device is written only when its position changed.
/// - Nothing is allocated per pass: native buffers, preallocated handle arrays, structs; the loop thread's allocation
///   counter is reported in the stats line (it stays flat).
/// - A device that disappears releases its buttons and centres its hats; it is opened again every 2 s.
/// </remarks>
internal sealed class Forwarder : IDisposable
{
    private readonly ForwardingTable _table;
    private readonly IVJoySink _sink;
    private readonly XboxTable? _xbox;
    private readonly IXboxSink? _xboxSink;
    private XboxReport _xboxSent;
    private XboxReport _xboxMerged;
    private readonly VfRuntime? _functions;
    private readonly long _startTicks = Stopwatch.GetTimestamp();
    private readonly double _microsPerTick = 1_000_000.0 / Stopwatch.Frequency;
    private readonly int _rateHz;
    private readonly int _parentPid;
    private readonly Action<string> _output;
    private readonly DirectInputSource[] _sources;
    private readonly VJoyPosition[] _sent;
    private readonly object _gate = new();
    private readonly IntPtr _stopEvent;
    private readonly IntPtr _timer;
    private IntPtr _parentHandle;
    private IntPtr _dataFormat;
    private Native.IDirectInput8W? _directInput;
    private HiddenWindow? _window;
    private Thread? _thread;
    private bool _timerHighResolution;
    private long _passes;
    private long _devicesChangedAt;
    private long _wakes;
    private long _writes;
    private long _changes;
    private long _allocated;
    private long _allocatedAtStart;
    private volatile string _exitReason = "stopped";
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _events = new();

    public Forwarder(ForwarderConfig config, IVJoySink sink, int rateHz, int parentPid, Action<string> output, IXboxSink? xboxSink = null, IVfKeySink? keys = null)
    {
        _sink = sink;
        if (config.UsesXbox && xboxSink is not null)
        {
            _xbox = new XboxTable(config, sink.AxisMin, sink.AxisMax);
            _xboxSink = xboxSink;
        }
        _rateHz = Math.Clamp(rateHz, 60, 1000);
        _parentPid = parentPid;
        _output = output;
        _table = new ForwardingTable(config, sink.AxisMin, sink.AxisMax, sink.DiscreteHats);
        if (config.Functions.Count > 0)
        {
            _functions = new VfRuntime(config.Functions, config.Sources.Count, _table.DeviceIds.Length, sink.AxisMin, sink.AxisMax, keys);
            foreach (var problem in _functions.Problems)
                _events.Enqueue("error function: " + problem);
        }
        _sources = config.Sources.Select((s, i) => new DirectInputSource(i, s)).ToArray();
        _sent = new VJoyPosition[_table.DeviceIds.Length];
        _stopEvent = Native.CreateEventW(IntPtr.Zero, true, false, IntPtr.Zero);
        _timer = Native.CreateWaitableTimerExW(IntPtr.Zero, IntPtr.Zero, Native.CreateWaitableTimerHighResolution, Native.TimerAllAccess);
        _timerHighResolution = _timer != IntPtr.Zero;
        if (_timer == IntPtr.Zero)
            _timer = Native.CreateWaitableTimerExW(IntPtr.Zero, IntPtr.Zero, 0, Native.TimerAllAccess);
    }

    public ForwardingTable Table => _table;
    public XboxTable? Xbox => _xbox;
    /// <summary>The virtual functions (null without any); their State/Input/Active fields feed the live view.</summary>
    public VfRuntime? Functions => _functions;
    public int RateHz => _rateHz;
    public int PeriodMs => Math.Max(1, 1000 / _rateHz);
    public long Passes => Interlocked.Read(ref _passes);
    public long Wakes => Interlocked.Read(ref _wakes);
    /// <summary>Poll only (no DirectInput change events): the timer alone drives the passes.</summary>
    public bool PollOnly { get; init; }
    public long Writes => Interlocked.Read(ref _writes);
    public long Changes => Interlocked.Read(ref _changes);
    public long LoopAllocatedBytes => Interlocked.Read(ref _allocated);
    public string ExitReason => _exitReason;
    public int ConnectedSources => _sources.Count(s => s.Connected);
    public int SourceCount => _sources.Length;

    /// <summary>Opens DirectInput and the devices; the vJoy sink must be open already.</summary>
    public void Start()
    {
        _window = HiddenWindow.Create(() => Interlocked.Exchange(ref _devicesChangedAt, Stopwatch.GetTimestamp()));
        _directInput = CreateDirectInput();
        _dataFormat = BuildDataFormat();
        foreach (var source in _sources)
        {
            var ok = source.Open(_directInput, _window.Handle, _dataFormat, _table.AxisMin, _table.AxisMax);
            _output("source " + source.Index.ToString(CultureInfo.InvariantCulture) + " " + (ok ? "connected" : "missing") + " " + source.Source.Device + (ok ? "" : " (" + source.LastError + ")"));
            if (!ok)
                source.NextRetry = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 60;
        }

        if (_parentPid > 0)
            _parentHandle = Native.OpenProcess(Native.Synchronize, false, _parentPid);
        if (!_timerHighResolution)
            Native.timeBeginPeriod(1);
        // Neutral first: vJoy starts with its axes at the minimum (a held half axis in the game).
        for (var i = 0; i < _table.DeviceIds.Length; i++)
        {
            _sink.Update(_table.DeviceIds[i], ref _table.Position(i));
            _sent[i] = _table.Position(i);
        }

        if (_xbox is not null && _xboxSink is not null)
        {
            _xboxSent = _xbox.Report;
            _xboxSink.Update(ref _xboxSent);
        }

        _thread = new Thread(Loop) { IsBackground = true, Name = "JoyPro vJoy forwarder", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public void Stop(string reason)
    {
        _exitReason = reason;
        Native.SetEvent(_stopEvent);
    }

    public bool Wait(int milliseconds) => _thread?.Join(milliseconds) ?? true;

    /// <summary>Events of the loop (device lost/back), for the protocol output; taken by the reporting thread.</summary>
    public bool TryTakeEvent(out string message) => _events.TryDequeue(out message!);

    /// <summary>A copy of the vJoy positions for the live view.</summary>
    public void CopyPositions(VJoyPosition[] target)
    {
        lock (_gate)
        {
            for (var i = 0; i < Math.Min(target.Length, _sent.Length); i++)
                target[i] = _sent[i];
        }
    }

    private unsafe void Loop()
    {
        _allocatedAtStart = GC.GetAllocatedBytesForCurrentThread();
        var periodMs = PeriodMs;
        long due = -(long)periodMs * 10_000;
        Native.SetWaitableTimer(_timer, ref due, periodMs, IntPtr.Zero, IntPtr.Zero, false);
        var fixedHandles = _parentHandle != IntPtr.Zero ? 3 : 2;
        var count = PollOnly ? fixedHandles : Math.Min(64, fixedHandles + _sources.Length);
        var handles = new IntPtr[count];
        handles[0] = _stopEvent;
        handles[1] = _timer;
        if (_parentHandle != IntPtr.Zero)
            handles[2] = _parentHandle;
        for (var i = 0; i < count - fixedHandles; i++)
            handles[fixedHandles + i] = _sources[i].Event;
        var minEventGap = Stopwatch.Frequency / (2L * _rateHz);
        var lastPass = 0L;
        fixed (IntPtr* pointer = handles)
        {
            while (true)
            {
                var wait = Native.WaitForMultipleObjects((uint)count, pointer, false, Native.Infinite);
                if (wait == Native.WaitObject0)
                    break;
                if (wait == Native.WaitFailed)
                {
                    _exitReason = "wait failed";
                    break;
                }

                if (_parentHandle != IntPtr.Zero && wait == Native.WaitObject0 + 2)
                {
                    _exitReason = "parent exited";
                    break;
                }

                Interlocked.Increment(ref _wakes);
                var now = Stopwatch.GetTimestamp();
                var deviceEvent = wait >= Native.WaitObject0 + (uint)fixedHandles;
                if (deviceEvent && now - lastPass < minEventGap)
                    continue; // the next timer tick takes it
                lastPass = now;
                Pass(now);
                Interlocked.Exchange(ref _allocated, GC.GetAllocatedBytesForCurrentThread() - _allocatedAtStart);
            }
        }

        Native.CancelWaitableTimer(_timer);
        // Neutral at the end: no button stays pressed in the game, no key stays down.
        lock (_gate)
        {
            _functions?.ReleaseAll();
            _table.Reset();
            for (var i = 0; i < _table.DeviceIds.Length; i++)
            {
                _sink.Update(_table.DeviceIds[i], ref _table.Position(i));
                _sent[i] = _table.Position(i);
            }

            if (_xboxSink is not null)
            {
                _xboxSent = default;
                _xboxSink.Update(ref _xboxSent);
            }
        }
    }

    private void Pass(long now)
    {
        Interlocked.Increment(ref _passes);
        var changed = false;
        for (var i = 0; i < _sources.Length; i++)
        {
            var source = _sources[i];
            if (!source.Connected)
            {
                // Opening a device that is not there costs DirectInput a device scan (tens of ms), so a missing
                // device is tried again each second only for 10 s after Windows reported a device change
                // (WM_DEVICECHANGE), else once a minute.
                if (now - Interlocked.Read(ref _devicesChangedAt) < Stopwatch.Frequency * 10 && source.NextRetry - now > Stopwatch.Frequency)
                    source.NextRetry = now;
                if (now >= source.NextRetry)
                    Reconnect(source, now);
                continue;
            }

            var result = source.Read();
            if (result < 0)
            {
                lock (_gate)
                {
                    _table.Release(i);
                    _xbox?.Release(i);
                    _functions?.ReleaseSource(i);
                }
                source.CloseDevice();
                source.NextRetry = now + Stopwatch.Frequency;
                _events.Enqueue("source " + i.ToString(CultureInfo.InvariantCulture) + " lost " + source.Source.Device);
                changed = true;
                continue;
            }

            if (result > 0)
            {
                lock (_gate)
                {
                    _table.Apply(i, source.State);
                    _xbox?.Apply(i, source.State);
                    if (_functions is not null)
                        source.State.CopyTo(_functions.State(i));
                }
                changed = true;
            }
        }

        if (changed)
            Interlocked.Increment(ref _changes);
        // Virtual functions run every pass (pulses, turbo, macros and long presses need the clock, not only changes).
        if (_functions is not null)
        {
            lock (_gate)
            {
                _functions.Tick((long)((now - _startTicks) * _microsPerTick));
                _table.MergeFunctions(_functions);
                if (_xbox is not null)
                    _xbox.MergeFunctions(_functions, ref _xboxMerged);
            }
        }
        else if (!changed)
            return;
        lock (_gate)
        {
            for (var d = 0; d < _sent.Length; d++)
            {
                ref var position = ref _table.Position(d);
                if (Same(ref position, ref _sent[d]))
                    continue;
                _sink.Update(_table.DeviceIds[d], ref position);
                _sent[d] = position;
                Interlocked.Increment(ref _writes);
            }

            if (_xbox is not null && _xboxSink is not null)
            {
                ref var report = ref (_functions is not null ? ref _xboxMerged : ref _xbox.Report);
                if (!SameReport(ref report, ref _xboxSent))
                {
                    _xboxSink.Update(ref report);
                    _xboxSent = report;
                    Interlocked.Increment(ref _writes);
                }
            }
        }
    }

    private void Reconnect(DirectInputSource source, long now)
    {
        if (_directInput is null || _window is null)
            return;
        if (source.Open(_directInput, _window.Handle, _dataFormat, _table.AxisMin, _table.AxisMax))
            _events.Enqueue("source " + source.Index.ToString(CultureInfo.InvariantCulture) + " connected " + source.Source.Device);
        else
            source.NextRetry = now + Stopwatch.Frequency * (now - Interlocked.Read(ref _devicesChangedAt) < Stopwatch.Frequency * 10 ? 1 : 60);
    }

    private static bool SameReport(ref XboxReport a, ref XboxReport b) =>
        a.Buttons == b.Buttons && a.LeftTrigger == b.LeftTrigger && a.RightTrigger == b.RightTrigger && a.LeftX == b.LeftX && a.LeftY == b.LeftY && a.RightX == b.RightX && a.RightY == b.RightY;

    /// <summary>A copy of the Xbox report for the live view (false without an Xbox pad).</summary>
    public bool CopyXbox(out XboxReport report)
    {
        lock (_gate)
            report = _xboxSent;
        return _xbox is not null;
    }

    /// <summary>The live view line of the Xbox pad: "xout &lt;LX&gt; &lt;LY&gt; &lt;RX&gt; &lt;RY&gt; &lt;LT&gt; &lt;RT&gt; &lt;buttons hex&gt;".</summary>
    public static string DescribeXbox(ref XboxReport r, StringBuilder sb)
    {
        sb.Clear();
        sb.Append("xout ").Append(r.LeftX).Append(' ').Append(r.LeftY).Append(' ').Append(r.RightX).Append(' ').Append(r.RightY).Append(' ')
            .Append(r.LeftTrigger).Append(' ').Append(r.RightTrigger).Append(' ').Append(r.Buttons.ToString("X4", CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    private static bool Same(ref VJoyPosition a, ref VJoyPosition b) =>
        MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref a, 1)).SequenceEqual(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref b, 1)));

    private static Native.IDirectInput8W CreateDirectInput()
    {
        var iid = Native.IidIDirectInput8W;
        var hr = Native.DirectInput8Create(Native.GetModuleHandle(null), Native.DirectInputVersion, ref iid, out var directInput, IntPtr.Zero);
        if (hr < 0 || directInput is null)
            throw new COMException("DirectInput8Create failed", hr);
        return directInput;
    }

    /// <summary>
    /// JoyPro's data format (c_dfDIJoystick2's first 176 bytes): X Y Z Rx Ry Rz Slider Slider by GUID, 4 POVs, 128
    /// buttons, every object optional - the same numbering DCS and JoyPro's binds use (JOY_BTN1 = first button).
    /// </summary>
    private static IntPtr BuildDataFormat()
    {
        const int objectSize = 24;  // DIOBJECTDATAFORMAT on x64
        const int formatSize = 32;  // DIDATAFORMAT on x64
        const int objects = 8 + 4 + 128;
        const uint optional = 0x80000000;
        const uint anyInstance = 0x00FFFF00;
        const uint axis = 0x03;
        const uint pov = 0x10;
        const uint button = 0x0C;
        const uint aspectPosition = 0x100;
        Guid[] guids = [Native.GuidXAxis, Native.GuidYAxis, Native.GuidZAxis, Native.GuidRxAxis, Native.GuidRyAxis, Native.GuidRzAxis, Native.GuidSlider, Native.GuidPov];
        var memory = Marshal.AllocHGlobal(formatSize + objects * objectSize + guids.Length * 16);
        var table = memory + formatSize;
        var guidBase = table + objects * objectSize;
        for (var i = 0; i < guids.Length; i++)
            Marshal.StructureToPtr(guids[i], guidBase + i * 16, false);
        for (var i = 0; i < objects; i++)
        {
            var o = table + i * objectSize;
            IntPtr guid;
            int offset;
            uint type;
            uint flags;
            if (i < 8)
            {
                guid = guidBase + Math.Min(i, 6) * 16;
                offset = i * 4;
                type = axis;
                flags = aspectPosition;
            }
            else if (i < 12)
            {
                guid = guidBase + 7 * 16;
                offset = ForwardingTable.HatOffset + (i - 8) * 4;
                type = pov;
                flags = 0;
            }
            else
            {
                guid = IntPtr.Zero;
                offset = ForwardingTable.ButtonOffset + (i - 12);
                type = button;
                flags = 0;
            }

            Marshal.WriteIntPtr(o, 0, guid);
            Marshal.WriteInt32(o, 8, offset);
            Marshal.WriteInt32(o, 12, unchecked((int)(type | anyInstance | optional)));
            Marshal.WriteInt32(o, 16, (int)flags);
            Marshal.WriteInt32(o, 20, 0);
        }

        Marshal.WriteInt32(memory, 0, formatSize);
        Marshal.WriteInt32(memory, 4, objectSize);
        Marshal.WriteInt32(memory, 8, 1);   // DIDF_ABSAXIS
        Marshal.WriteInt32(memory, 12, ForwardingTable.StateSize);
        Marshal.WriteInt32(memory, 16, objects);
        Marshal.WriteInt32(memory, 20, 0);
        Marshal.WriteIntPtr(memory, 24, table);
        return memory;
    }

    public void Dispose()
    {
        Stop(_exitReason);
        Wait(3000);
        foreach (var source in _sources)
            source.Dispose();
        if (_directInput is not null)
        {
            Marshal.ReleaseComObject(_directInput);
            _directInput = null;
        }

        _window?.Dispose();
        _window = null;
        _xboxSink?.Dispose();
        if (_dataFormat != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_dataFormat);
            _dataFormat = IntPtr.Zero;
        }

        if (!_timerHighResolution)
            Native.timeEndPeriod(1);
        if (_parentHandle != IntPtr.Zero)
            Native.CloseHandle(_parentHandle);
        if (_timer != IntPtr.Zero)
            Native.CloseHandle(_timer);
        if (_stopEvent != IntPtr.Zero)
            Native.CloseHandle(_stopEvent);
    }

    /// <summary>The live view line of one vJoy device: "out &lt;id&gt; &lt;8 axes&gt; &lt;buttons hex&gt; &lt;4 hats&gt;".</summary>
    public static string Describe(ref VJoyPosition p, StringBuilder sb)
    {
        sb.Clear();
        sb.Append("out ").Append(p.bDevice.ToString(CultureInfo.InvariantCulture));
        for (var axis = 0; axis < 8; axis++)
            sb.Append(' ').Append(p.GetAxis(axis).ToString(CultureInfo.InvariantCulture));
        sb.Append(' ')
            .Append(((uint)p.lButtonsEx3).ToString("X8", CultureInfo.InvariantCulture))
            .Append(((uint)p.lButtonsEx2).ToString("X8", CultureInfo.InvariantCulture))
            .Append(((uint)p.lButtonsEx1).ToString("X8", CultureInfo.InvariantCulture))
            .Append(((uint)p.lButtons).ToString("X8", CultureInfo.InvariantCulture));
        for (var hat = 0; hat < 4; hat++)
            sb.Append(' ').Append(p.GetHat(hat) == uint.MaxValue ? "-1" : p.GetHat(hat).ToString(CultureInfo.InvariantCulture));
        return sb.ToString();
    }
}
