using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace JoyPro.Gui.Services.Components;

// What is on the PC now (read-only).
public sealed record ComponentState
{
    public bool Installed { get; init; }

    public bool Running { get; init; }

    public string Version { get; init; } = string.Empty;

    public string InstallFolder { get; init; } = string.Empty;

    public string Details { get; init; } = string.Empty;

    public static ComponentState Missing { get; } = new();
}

public interface IComponentProbe
{
    ComponentState Probe(ComponentSpec spec);
}

// Windows' installed-apps list (HKLM ...\Uninstall, 64- and 32-bit views) and the driver's service (installed = the
// service exists, running = the service runs). Nothing is changed.
[SupportedOSPlatform("windows")]
public sealed class WindowsComponentProbe : IComponentProbe
{
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public ComponentState Probe(ComponentSpec spec)
    {
        string version = string.Empty;
        string folder = string.Empty;
        bool listed = false;
        try
        {
            foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                using RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using RegistryKey? uninstall = root.OpenSubKey(UninstallKey);
                if (uninstall == null)
                    continue;
                foreach (string name in uninstall.GetSubKeyNames())
                {
                    using RegistryKey? app = uninstall.OpenSubKey(name);
                    if (app?.GetValue("DisplayName") is not string display || display.IndexOf(spec.DisplayNameContains, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    listed = true;
                    if (app.GetValue("DisplayVersion") is string displayVersion && displayVersion.Length > 0)
                        version = displayVersion;
                    if (app.GetValue("InstallLocation") is string location && location.Length > 0)
                        folder = location.TrimEnd('\\');
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }

        ServiceStatus.State service = ServiceStatus.Query(spec.ServiceName);
        bool installed = service != ServiceStatus.State.Missing;
        string details = string.Empty;
        if (installed && spec.RequiredFile.Length > 0)
        {
            string file = Path.Combine(folder.Length > 0 ? folder : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), spec.Name), spec.RequiredFile);
            if (!File.Exists(file))
            {
                installed = false;
                details = T("the driver is there, but {0} is missing", file);
            }
        }
        else if (!installed && listed)
        {
            details = T("listed in Windows' apps, but its driver service is missing");
        }
        return new ComponentState
        {
            Installed = installed,
            Running = service == ServiceStatus.State.Running,
            Version = version,
            InstallFolder = folder,
            Details = details
        };
    }
}

// The state of a Windows service or driver (QueryServiceStatus with query rights only; no administrator needed).
[SupportedOSPlatform("windows")]
internal static class ServiceStatus
{
    public enum State
    {
        Missing,
        Stopped,
        Running
    }

    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceRunning = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusData
    {
        public uint dwServiceType;
        public uint dwCurrentState;
        public uint dwControlsAccepted;
        public uint dwWin32ExitCode;
        public uint dwServiceSpecificExitCode;
        public uint dwCheckPoint;
        public uint dwWaitHint;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManagerW(string? machineName, string? databaseName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenServiceW(IntPtr manager, string serviceName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceStatus(IntPtr service, out ServiceStatusData status);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr handle);

    public static State Query(string name)
    {
        IntPtr manager = OpenSCManagerW(null, null, ScManagerConnect);
        if (manager == IntPtr.Zero)
            return State.Missing;
        try
        {
            IntPtr service = OpenServiceW(manager, name, ServiceQueryStatus);
            if (service == IntPtr.Zero)
                return State.Missing;
            try
            {
                return QueryServiceStatus(service, out ServiceStatusData status) && status.dwCurrentState == ServiceRunning ? State.Running : State.Stopped;
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }
}

public static class ComponentVersion
{
    // -1 installed older than pinned, 0 same (or unknown), 1 newer. "1.5.230" vs "1.5.230.0" are the same.
    public static int Compare(string installed, string pinned)
    {
        int[] a = Parts(installed);
        int[] b = Parts(pinned);
        if (a.Length == 0 || b.Length == 0)
            return 0;
        for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            int x = i < a.Length ? a[i] : 0;
            int y = i < b.Length ? b[i] : 0;
            if (x != y)
                return x < y ? -1 : 1;
        }
        return 0;
    }

    private static int[] Parts(string version)
    {
        string[] parts = (version ?? string.Empty).Trim().TrimStart('v', 'V').Split('.');
        int[] numbers = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            string digits = new(parts[i].TakeWhile(char.IsDigit).ToArray());
            if (!int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out numbers[i]))
                return [];
        }
        return numbers;
    }
}
