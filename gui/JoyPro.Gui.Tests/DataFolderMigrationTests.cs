using System;
using System.IO;
using System.Linq;
using JoyPro.Logging;
using Xunit;

namespace JoyPro.Gui.Tests;

// The one data folder (engine/JoyProDataFolder.cs, docs/installs.md "Data folder"): an old %LOCALAPPDATA%\JoyPro is
// moved into %LOCALAPPDATA%\JoyPro2 without losing a file: a fresh move, a move that stopped in the middle, a file in
// use, a second start after the move (nothing to do, nothing to log), the junction 2.4.0 left, and a portable setup
// (JOYPRO_DATA_DIR) that never touches %LOCALAPPDATA%. Every case runs in a temp folder standing in for %LOCALAPPDATA%.
[Collection("JoyProDataDir")]
public sealed class DataFolderMigrationTests : IDisposable
{
    private readonly string local = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "migration-" + Guid.NewGuid().ToString("N"));

    private string Legacy => Path.Combine(local, "JoyPro");

    private string Target => Path.Combine(local, "JoyPro2");

    public DataFolderMigrationTests() => Directory.CreateDirectory(local);

    public void Dispose()
    {
        try
        {
            // A junction is removed as a link (its target's files stay where they are).
            if (JoyProDataFolder.IsJunction(Legacy))
                Directory.Delete(Legacy);
            Directory.Delete(local, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void Write(string root, string relative, string content)
    {
        string path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private string Read(string root, string relative) => File.ReadAllText(Path.Combine(root, relative));

    // What a 2.3 PC has: the engine's data in JoyPro, the GUI's logs and pictures in JoyPro2.
    private void OldLayout()
    {
        Write(Legacy, @"GameData\DB\DCS\FA-18C_hornet.lua", "hornet");
        Write(Legacy, @"GameData\DB\IL2\commands.tsv", "il2");
        Write(Legacy, @"ProfileHistory\Holdi-1234567890\history.json", "{\"versions\":[]}");
        Write(Legacy, @"ProfileHistory\Holdi-1234567890\blobs\abc", "blob");
        Write(Legacy, @"Usage\sessions.jsonl", "{}");
        Write(Legacy, @"Sync\DCS.json", "sync");
        Write(Legacy, "game-installs.json", "{\"DCS\":\"D:\\\\DCS\"}");
        Write(Target, @"Logs\gui-2026-10-20.log", "log");
        Write(Target, @"DeviceImages\vpc.png", "png");
    }

    private void AssertAllMoved()
    {
        Assert.Equal("hornet", Read(Target, @"GameData\DB\DCS\FA-18C_hornet.lua"));
        Assert.Equal("il2", Read(Target, @"GameData\DB\IL2\commands.tsv"));
        Assert.Equal("blob", Read(Target, @"ProfileHistory\Holdi-1234567890\blobs\abc"));
        Assert.Equal("{}", Read(Target, @"Usage\sessions.jsonl"));
        Assert.Equal("sync", Read(Target, @"Sync\DCS.json"));
        Assert.Contains("DCS", Read(Target, "game-installs.json"));
        Assert.Equal("log", Read(Target, @"Logs\gui-2026-10-20.log"));
        Assert.Equal("png", Read(Target, @"DeviceImages\vpc.png"));
    }

    [Fact]
    public void FreshMove_PutsEverythingIntoJoyPro2_AndOnlyJoyPro2IsLeft()
    {
        OldLayout();

        DataMigrationResult result = JoyProDataFolder.Migrate(local);

        Assert.Equal("done", result.Status);
        AssertAllMoved();
        Assert.Contains("\"status\": \"done\"", Read(Target, JoyProDataFolder.StateFileName));
        // No junction any more (it looked like a second data folder): the old folder is simply gone.
        Assert.True(result.OldFolderRemoved);
        Assert.True(result.IsNews);
        Assert.False(Directory.Exists(Legacy));
        Assert.Contains("\"oldFolderRemoved\": true", Read(Target, JoyProDataFolder.StateFileName));
    }

    [Fact]
    public void AlreadyMoved_ChangesNothing_AndIsNoNews()
    {
        OldLayout();
        Assert.Equal("done", JoyProDataFolder.Migrate(local).Status);
        DateTime stamp = File.GetLastWriteTimeUtc(Path.Combine(Target, JoyProDataFolder.StateFileName));

        DataMigrationResult again = JoyProDataFolder.Migrate(local);

        Assert.Equal("none", again.Status);
        Assert.Equal(0, again.Moved);
        Assert.False(again.IsNews);
        AssertAllMoved();
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(Path.Combine(Target, JoyProDataFolder.StateFileName)));
    }

    // 2.4.0 put a junction JoyPro -> JoyPro2 in place of the old folder: the next start removes it as a link (JoyPro2's
    // files stay); a junction to any other folder is the user's and stays.
    [Fact]
    public void JunctionFrom240_IsRemoved_ButAForeignJunctionStays()
    {
        OldLayout();
        Assert.Equal("done", JoyProDataFolder.Migrate(local).Status);
        Assert.True(MakeJunction(Legacy, Target));
        Assert.True(File.Exists(Path.Combine(Legacy, "game-installs.json")));

        DataMigrationResult removed = JoyProDataFolder.Migrate(local);

        Assert.True(removed.OldFolderRemoved);
        Assert.False(Directory.Exists(Legacy));
        AssertAllMoved();
        Assert.Equal("none", JoyProDataFolder.Migrate(local).Status);

        string other = Path.Combine(local, "Other");
        Write(other, "mine.txt", "mine");
        Assert.True(MakeJunction(Legacy, other));

        DataMigrationResult kept = JoyProDataFolder.Migrate(local);

        Assert.False(kept.OldFolderRemoved);
        Assert.False(kept.IsNews);
        Assert.True(JoyProDataFolder.IsJunction(Legacy));
        Assert.Equal("mine", Read(Legacy, "mine.txt"));
    }

    [Fact]
    public void EmptyOldFolder_IsRemoved()
    {
        Write(Target, @"Logs\gui.log", "log");
        Directory.CreateDirectory(Path.Combine(Legacy, "GameData"));

        DataMigrationResult result = JoyProDataFolder.Migrate(local);

        Assert.True(result.OldFolderRemoved);
        Assert.False(Directory.Exists(Legacy));
        Assert.Equal("log", Read(Target, @"Logs\gui.log"));
    }

    private static bool MakeJunction(string link, string target)
    {
        System.Diagnostics.ProcessStartInfo info = new("cmd.exe", "/c mklink /J \"" + link + "\" \"" + target + "\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using System.Diagnostics.Process? process = System.Diagnostics.Process.Start(info);
        if (process == null)
            return false;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit(10000);
        return JoyProDataFolder.IsJunction(link);
    }

    [Fact]
    public void NoOldFolder_NothingToDo()
    {
        Write(Target, @"Logs\gui.log", "log");
        DataMigrationResult result = JoyProDataFolder.Migrate(local);
        Assert.Equal("none", result.Status);
        Assert.False(Directory.Exists(Legacy));
        Assert.False(File.Exists(Path.Combine(Target, JoyProDataFolder.StateFileName)));
    }

    // A start that stopped in the middle (power cut, killed process): part of the folders moved, a file that is in both
    // places with the same content (moved, not yet deleted), a file both have with different content, a copy left over.
    [Fact]
    public void InterruptedMove_GoesOnWhereItStopped_AndLosesNothing()
    {
        OldLayout();
        Directory.CreateDirectory(Target);
        Directory.Move(Path.Combine(Legacy, "Usage"), Path.Combine(Target, "Usage"));
        Write(Target, @"GameData\DB\DCS\FA-18C_hornet.lua", "hornet");
        Write(Target, "game-installs.json", "{\"DCS\":\"E:\\\\DCS\"}");
        Write(Legacy, @"Sync\DCS.json.joypro-moving", "half a copy");
        Write(Target, @"Sync\DCS.json.joypro-moving", "half a copy");
        Write(Target, JoyProDataFolder.StateFileName, "{\"status\": \"moving\"}");

        DataMigrationResult result = JoyProDataFolder.Migrate(local);

        Assert.Equal("done", result.Status);
        Assert.Equal("{}", Read(Target, @"Usage\sessions.jsonl"));
        Assert.Equal("hornet", Read(Target, @"GameData\DB\DCS\FA-18C_hornet.lua"));
        Assert.Equal("il2", Read(Target, @"GameData\DB\IL2\commands.tsv"));
        // JoyPro2's own copy stays; the other one is kept, never dropped.
        Assert.Contains("E:", Read(Target, "game-installs.json"));
        Assert.Contains("D:", Read(Target, Path.Combine(JoyProDataFolder.ConflictFolderName, "game-installs.json")));
        Assert.Contains("game-installs.json", result.Conflicts);
        Assert.False(File.Exists(Path.Combine(Target, @"Sync\DCS.json.joypro-moving")));
        Assert.Equal("sync", Read(Target, @"Sync\DCS.json"));
        Assert.Contains("\"status\": \"done\"", Read(Target, JoyProDataFolder.StateFileName));
    }

    // A file in use (an older JoyPro2 still running) stays; everything else moves; the next start moves the rest.
    [Fact]
    public void LockedFile_StaysWithANote_AndMovesOnTheNextStart()
    {
        OldLayout();
        string locked = Path.Combine(Legacy, @"ProfileHistory\Holdi-1234567890\history.json");
        DataMigrationResult first;
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read))
            first = JoyProDataFolder.Migrate(local);

        Assert.Equal("partial", first.Status);
        Assert.Contains(first.Left, l => l.EndsWith("history.json", StringComparison.OrdinalIgnoreCase));
        Assert.True(File.Exists(locked));
        Assert.True(File.Exists(Path.Combine(Legacy, JoyProDataFolder.MovedNoteName)));
        Assert.Equal("hornet", Read(Target, @"GameData\DB\DCS\FA-18C_hornet.lua"));
        Assert.Equal("blob", Read(Target, @"ProfileHistory\Holdi-1234567890\blobs\abc"));
        Assert.Contains("\"status\": \"partial\"", Read(Target, JoyProDataFolder.StateFileName));

        DataMigrationResult second = JoyProDataFolder.Migrate(local);

        Assert.Equal("done", second.Status);
        Assert.Equal("{\"versions\":[]}", Read(Target, @"ProfileHistory\Holdi-1234567890\history.json"));
        Assert.False(File.Exists(Path.Combine(Target, JoyProDataFolder.MovedNoteName)));
        AssertAllMoved();
    }

    // JOYPRO_DATA_DIR (a portable setup, the tests): that folder is the data folder and %LOCALAPPDATA% is never touched,
    // even with the migration allowed (the installed app without it: engine smoke part smoke\datafolder.ps1, which runs
    // the engine with JOYPRO_LOCALAPPDATA on a temp folder; here JOYPRO_DATA_DIR always stays set, so no engine a
    // parallel test starts could ever reach the user's folder).
    [Fact]
    public void PortableSetup_UsesItsOwnFolder_AndNeverMovesLocalAppData()
    {
        OldLayout();
        string? data = Environment.GetEnvironmentVariable(JoyProDataFolder.Variable);
        string? noMigration = Environment.GetEnvironmentVariable(JoyProDataFolder.NoMigrationVariable);
        string? overrideBefore = JoyProDataFolder.LocalAppDataOverride;
        string portable = Path.Combine(local, "portable", "data");
        try
        {
            Environment.SetEnvironmentVariable(JoyProDataFolder.Variable, portable);
            Environment.SetEnvironmentVariable(JoyProDataFolder.NoMigrationVariable, null);
            JoyProDataFolder.LocalAppDataOverride = local;
            JoyProDataFolder.ResetCheck();

            Assert.True(JoyProDataFolder.IsCustom);
            Assert.Equal(portable, JoyProDataFolder.Root());
            JoyProDataFolder.EnsureMigrated();

            Assert.Equal("hornet", Read(Legacy, @"GameData\DB\DCS\FA-18C_hornet.lua"));
            Assert.False(JoyProDataFolder.IsJunction(Legacy));
            Assert.False(File.Exists(Path.Combine(Target, "game-installs.json")));
            Assert.False(File.Exists(Path.Combine(Target, JoyProDataFolder.StateFileName)));
        }
        finally
        {
            Environment.SetEnvironmentVariable(JoyProDataFolder.NoMigrationVariable, noMigration);
            Environment.SetEnvironmentVariable(JoyProDataFolder.Variable, data);
            JoyProDataFolder.LocalAppDataOverride = overrideBefore;
            JoyProDataFolder.ResetCheck();
        }
    }

    [Fact]
    public void EveryPartOfJoyPro_UsesTheOneDataFolder()
    {
        string root = JoyProDataFolder.Root();
        Assert.Equal(root, Environment.GetEnvironmentVariable(JoyProDataFolder.Variable));
        Assert.Equal(root, JoyPro.Gui.Services.GameDataLocations.DataRoot());
        Assert.Equal(root, JoyPro.Gui.Services.AppInfo.DataFolder);
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(JoyProLog.FolderVariable)))
            Assert.Equal(Path.Combine(root, "Logs"), JoyProLog.DefaultFolder());
        if (string.IsNullOrWhiteSpace(JoyPro.History.ProfileHistory.RootOverride))
            Assert.Equal(Path.Combine(root, "ProfileHistory"), JoyPro.History.ProfileHistory.Root());
        Assert.Equal(root, JoyPro.DeviceReference.DeviceCatalog.DataRoot());
        Assert.Equal(Path.Combine(root, "Ffb", "Telemetry"), JoyPro.Ffb.Core.Host.TelemetryFfbPaths.Folder());
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("JOYPRO_DEVICE_IMAGE_CACHE")))
            Assert.StartsWith(root, JoyPro.Gui.Services.DeviceImageCache.CacheFolder(), StringComparison.OrdinalIgnoreCase);
    }
}
