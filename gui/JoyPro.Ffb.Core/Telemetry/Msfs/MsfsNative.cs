using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace JoyPro.Ffb.Core.Telemetry.Msfs;

/// <summary>The user's own native SimConnect.dll (never shipped with JoyPro: docs/licensing-audit.md), loaded with
/// NativeLibrary from the path <see cref="SimConnectLocator"/> found. Read only: JoyPro binds no call that sends
/// events or sets data.</summary>
/// <remarks>The library stays loaded for the life of the process (SimConnect runs its own receive thread; unloading
/// it under that thread is unsafe). One instance = one connection at a time; Open after Close reconnects.</remarks>
public sealed class NativeSimConnectClient : ISimConnectClient
{
    private const int MaxMessage = 1 << 20;
    private static readonly Dictionary<string, IntPtr> Libraries = new(StringComparer.OrdinalIgnoreCase);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int OpenFn(out IntPtr handle, [MarshalAs(UnmanagedType.LPStr)] string name, IntPtr hwnd, uint userEventWin32, IntPtr eventHandle, uint configIndex);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CloseFn(IntPtr handle);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int AddToDataDefinitionFn(IntPtr handle, uint defineId, [MarshalAs(UnmanagedType.LPStr)] string datumName,
        [MarshalAs(UnmanagedType.LPStr)] string? unitsName, uint datumType, float epsilon, uint datumId);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ClearDataDefinitionFn(IntPtr handle, uint defineId);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int RequestDataOnSimObjectFn(IntPtr handle, uint requestId, uint defineId, uint objectId, uint period, uint flags, uint origin, uint interval, uint limit);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int RequestDataOnSimObjectTypeFn(IntPtr handle, uint requestId, uint defineId, uint radiusMeters, uint type);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SubscribeToSystemEventFn(IntPtr handle, uint eventId, [MarshalAs(UnmanagedType.LPStr)] string systemEventName);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int RequestSystemStateFn(IntPtr handle, uint requestId, [MarshalAs(UnmanagedType.LPStr)] string state);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetNextDispatchFn(IntPtr handle, out IntPtr data, out uint size);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetLastSentPacketIdFn(IntPtr handle, out uint packetId);

    private readonly OpenFn _open;
    private readonly CloseFn _close;
    private readonly AddToDataDefinitionFn _add;
    private readonly ClearDataDefinitionFn _clear;
    private readonly RequestDataOnSimObjectFn _request;
    private readonly RequestDataOnSimObjectTypeFn _requestByType;
    private readonly SubscribeToSystemEventFn _subscribe;
    private readonly RequestSystemStateFn _state;
    private readonly GetNextDispatchFn _next;
    private readonly GetLastSentPacketIdFn _lastPacket;
    private byte[] _buffer = new byte[4096];
    private IntPtr _handle;

    /// <exception cref="DllNotFoundException">The file cannot be loaded.</exception>
    /// <exception cref="EntryPointNotFoundException">It is not a SimConnect client library.</exception>
    public NativeSimConnectClient(string dllPath)
    {
        Location = Path.GetFullPath(dllPath);
        IntPtr library;
        lock (Libraries)
        {
            if (!Libraries.TryGetValue(Location, out library))
            {
                library = NativeLibrary.Load(Location);
                Libraries[Location] = library;
            }
        }

        _open = Bind<OpenFn>(library, "SimConnect_Open");
        _close = Bind<CloseFn>(library, "SimConnect_Close");
        _add = Bind<AddToDataDefinitionFn>(library, "SimConnect_AddToDataDefinition");
        _clear = Bind<ClearDataDefinitionFn>(library, "SimConnect_ClearDataDefinition");
        _request = Bind<RequestDataOnSimObjectFn>(library, "SimConnect_RequestDataOnSimObject");
        _requestByType = Bind<RequestDataOnSimObjectTypeFn>(library, "SimConnect_RequestDataOnSimObjectType");
        _subscribe = Bind<SubscribeToSystemEventFn>(library, "SimConnect_SubscribeToSystemEvent");
        _state = Bind<RequestSystemStateFn>(library, "SimConnect_RequestSystemState");
        _next = Bind<GetNextDispatchFn>(library, "SimConnect_GetNextDispatch");
        _lastPacket = Bind<GetLastSentPacketIdFn>(library, "SimConnect_GetLastSentPacketID");
    }

    private static T Bind<T>(IntPtr library, string name) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));

    public string Location { get; }

    public bool Open(string applicationName, WaitHandle dataReady)
    {
        Close();
        var hr = _open(out var handle, applicationName, IntPtr.Zero, 0, dataReady.SafeWaitHandle.DangerousGetHandle(), 0);
        if (hr < 0 || handle == IntPtr.Zero)
            return false;
        _handle = handle;
        return true;
    }

    public void Close()
    {
        var handle = _handle;
        _handle = IntPtr.Zero;
        if (handle != IntPtr.Zero)
            _close(handle);
    }

    public bool AddToDataDefinition(uint defineId, string datumName, string? unitsName, SimConnectDataType type, uint datumId) =>
        _handle != IntPtr.Zero && _add(_handle, defineId, datumName, unitsName, (uint)type, 0f, datumId) >= 0;

    public bool ClearDataDefinition(uint defineId) => _handle != IntPtr.Zero && _clear(_handle, defineId) >= 0;

    public uint LastSentPacketId() => _handle != IntPtr.Zero && _lastPacket(_handle, out var id) >= 0 ? id : 0;

    public bool RequestDataOnSimObject(uint requestId, uint defineId, uint objectId, SimConnectPeriod period, SimConnectRequestFlags flags, uint interval) =>
        _handle != IntPtr.Zero && _request(_handle, requestId, defineId, objectId, (uint)period, (uint)flags, 0, interval, 0) >= 0;

    public bool RequestDataOnSimObjectType(uint requestId, uint defineId, uint radiusMeters, SimConnectSimObjectType type) =>
        _handle != IntPtr.Zero && _requestByType(_handle, requestId, defineId, radiusMeters, (uint)type) >= 0;

    public bool SubscribeToSystemEvent(uint eventId, string systemEventName) =>
        _handle != IntPtr.Zero && _subscribe(_handle, eventId, systemEventName) >= 0;

    public bool RequestSystemState(uint requestId, string state) => _handle != IntPtr.Zero && _state(_handle, requestId, state) >= 0;

    public bool TryGetNextDispatch(out ReadOnlySpan<byte> message)
    {
        message = default;
        if (_handle == IntPtr.Zero || _next(_handle, out var data, out var size) < 0 || data == IntPtr.Zero || size < SimConnectLayout.HeaderSize)
            return false;
        var length = (int)Math.Min(size, MaxMessage);
        if (_buffer.Length < length)
            _buffer = new byte[Math.Max(length, _buffer.Length * 2)];
        Marshal.Copy(data, _buffer, 0, length);
        message = new ReadOnlySpan<byte>(_buffer, 0, length);
        return true;
    }

    public void Dispose() => Close();
}

/// <summary>Finds the user's SimConnect.dll for MSFS 2020 / 2024.</summary>
/// <remarks>Order: the configured file (or folder), the folder of the known game exe, the folder of the running sim,
/// the sim's install folders (Steam libraries, Xbox app <c>XboxGames\...\Content</c>), the SDKs (<c>MSFS_SDK</c>,
/// <c>MSFS2024_SDK</c>, <c>C:\MSFS SDK</c>, <c>C:\MSFS 2024 SDK</c>: <c>SimConnect SDK\lib\SimConnect.dll</c>), then for
/// MSFS 2024 the MSFS 2020 copies (the 2024 server accepts 2020 clients). MSFS 2020 ships <c>SimConnect.dll</c> in its
/// install folder; Steam MSFS 2024 ships only <c>SimConnect_internal.dll</c>, which JoyPro never loads (install the
/// MSFS 2024 SDK).</remarks>
public sealed class SimConnectLocator
{
    public const string DllName = "SimConnect.dll";
    public const string Msfs2024InternalDll = "SimConnect_internal.dll";

    private readonly bool _is2024;
    private readonly string? _configured;
    private readonly string? _gameExe;
    private List<string>? _installs;

    public SimConnectLocator(string gameId, string? configuredDll, string? gameExePath)
    {
        _is2024 = !string.Equals(gameId, "MSFS2020", StringComparison.OrdinalIgnoreCase);
        _configured = string.IsNullOrWhiteSpace(configuredDll) ? null : configuredDll;
        _gameExe = string.IsNullOrWhiteSpace(gameExePath) ? null : gameExePath;
    }

    /// <summary>Folders the last <see cref="Find"/> looked in (for the problem text).</summary>
    public IReadOnlyList<string> Searched { get; private set; } = [];

    /// <summary>The first existing candidate, else null (see <see cref="Searched"/>).</summary>
    public string? Find()
    {
        var searched = new List<string>();
        foreach (var path in Candidates())
        {
            var folder = Path.GetDirectoryName(path) ?? path;
            if (!searched.Contains(folder, StringComparer.OrdinalIgnoreCase))
                searched.Add(folder);
            try
            {
                if (File.Exists(path))
                {
                    Searched = searched;
                    return path;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        Searched = searched;
        return null;
    }

    private IEnumerable<string> Candidates()
    {
        if (_configured is not null)
        {
            yield return Directory.Exists(_configured) ? Path.Combine(_configured, DllName) : _configured;
            yield break;
        }

        var ownFolders = new List<string>();
        if (_gameExe is not null && Path.GetDirectoryName(_gameExe) is { Length: > 0 } exeFolder)
            ownFolders.Add(exeFolder);
        ownFolders.AddRange(RunningSimFolders(_is2024 ? "FlightSimulator2024" : "FlightSimulator"));
        _installs ??= InstallFolders();
        var own = _installs.Where(f => IsFolderOf(f, _is2024)).ToList();
        var other = _installs.Where(f => !IsFolderOf(f, _is2024)).ToList();
        ownFolders.AddRange(own);

        foreach (var folder in ownFolders)
            yield return Path.Combine(folder, DllName);
        foreach (var sdk in SdkFolders(_is2024))
            yield return Path.Combine(sdk, "SimConnect SDK", "lib", DllName);
        if (!_is2024)
            yield break;
        foreach (var folder in RunningSimFolders("FlightSimulator").Concat(other))
            yield return Path.Combine(folder, DllName);
        foreach (var sdk in SdkFolders(false))
            yield return Path.Combine(sdk, "SimConnect SDK", "lib", DllName);
        // Not SimConnect_internal.dll (Steam MSFS 2024 ships only that): it is the sim's own in-process library and
        // untested from outside; loading it into another process is not worth the risk. 2024 users install the SDK.
    }

    private static bool IsFolderOf(string folder, bool is2024)
    {
        var name = folder.TrimEnd('\\', '/');
        var has2024 = name.Contains("2024", StringComparison.OrdinalIgnoreCase);
        return is2024 ? has2024 : !has2024;
    }

    private static IEnumerable<string> SdkFolders(bool is2024)
    {
        var env = Environment.GetEnvironmentVariable(is2024 ? "MSFS2024_SDK" : "MSFS_SDK");
        if (!string.IsNullOrWhiteSpace(env))
            yield return env.Trim().Trim('"');
        yield return is2024 ? @"C:\MSFS 2024 SDK" : @"C:\MSFS SDK";
    }

    /// <summary>Install folders of both sims (2020: Steam "MicrosoftFlightSimulator", Xbox "Microsoft Flight
    /// Simulator\Content"; 2024: Steam "MSFS2024", Xbox "Microsoft Flight Simulator 2024\Content").</summary>
    public static List<string> InstallFolders()
    {
        var folders = new List<string>();
        foreach (var library in SteamLibraries())
        {
            folders.Add(Path.Combine(library, "steamapps", "common", "MicrosoftFlightSimulator"));
            folders.Add(Path.Combine(library, "steamapps", "common", "MSFS2024"));
        }

        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType != DriveType.Fixed)
                    continue;
                var xbox = Path.Combine(drive.RootDirectory.FullName, "XboxGames");
                if (!Directory.Exists(xbox))
                    continue;
                folders.Add(Path.Combine(xbox, "Microsoft Flight Simulator", "Content"));
                folders.Add(Path.Combine(xbox, "Microsoft Flight Simulator 2024", "Content"));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return folders.Where(f =>
        {
            try
            {
                return Directory.Exists(f);
            }
            catch (Exception)
            {
                return false;
            }
        }).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IEnumerable<string> SteamLibraries()
    {
        var steamRoots = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                if (key?.GetValue("SteamPath") is string path && path.Length > 0)
                    steamRoots.Add(path.Replace('/', '\\'));
            }
            catch (Exception)
            {
            }
        }

        steamRoots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
        var libraries = new List<string>();
        foreach (var root in steamRoots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            libraries.Add(root);
            try
            {
                var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
                if (!File.Exists(vdf))
                    continue;
                foreach (Match match in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                    libraries.Add(match.Groups[1].Value.Replace(@"\\", @"\"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return libraries.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Folders of running processes with that name (QueryFullProcessImageName with the least access, so
    /// an elevated or packaged sim still answers).</summary>
    public static List<string> RunningSimFolders(string processName)
    {
        var folders = new List<string>();
        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(processName);
        }
        catch (Exception)
        {
            return folders;
        }

        foreach (var process in processes)
        {
            using (process)
            {
                if (ImagePath(process.Id) is { } path && Path.GetDirectoryName(path) is { Length: > 0 } folder)
                    folders.Add(folder);
            }
        }

        return folders;
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref uint size);

    private static string? ImagePath(int processId)
    {
        if (!OperatingSystem.IsWindows())
            return null;
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero)
            return null;
        try
        {
            var size = 1024u;
            var name = new StringBuilder((int)size);
            return QueryFullProcessImageName(handle, 0, name, ref size) ? name.ToString(0, (int)size) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }
}
