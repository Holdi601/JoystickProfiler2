using System.Runtime.CompilerServices;

namespace JoyPro.Ffb.Core.Tests;

// Every test run gets its own data folder: the hosts read telemetry-recording.json and telemetry-ffb.json from
// JOYPRO_DATA_DIR, and nothing a test does may reach the user's %LOCALAPPDATA%\JoyPro (recordings, settings, logs).
internal static class TestEnvironment
{
    public static string DataFolder { get; private set; } = "";

    [ModuleInitializer]
    internal static void Initialize()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("JOYPRO_DATA_DIR")))
        {
            DataFolder = Environment.GetEnvironmentVariable("JOYPRO_DATA_DIR")!;
            return;
        }

        DataFolder = Path.Combine(Path.GetTempPath(), "JoyPro.Ffb.Core.Tests", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(DataFolder);
        Environment.SetEnvironmentVariable("JOYPRO_DATA_DIR", DataFolder);
    }
}
