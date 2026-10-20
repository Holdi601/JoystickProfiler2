using System;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using JoyPro.Gui.ViewModels;
using JoyPro.Watch;
using Xunit;

namespace JoyPro.Gui.Tests;

// The pieces of the MSFS Store watcher chain on their own (MsfsStoreWatcherEndToEndTests runs it as a whole): what the
// GUI reads from engine sync status for the watcher (process folders, settle time, why a game is not watched, what an
// empty diff compared) and the watch list refreshed after an import.
[SupportedOSPlatform("windows")]
[Collection("TrayMode")]
public sealed class MsfsStoreWatcherPartsTests
{
    private const string Folders = ",\"ProcessFolders\":[\"E:\\\\XboxGames\\\\Microsoft Flight Simulator 2024\\\\Content\\\\\",\"C:\\\\Program Files\\\\WindowsApps\\\\Microsoft.Limitless_\"],\"SettleSeconds\":10";

    private const string Status =
        "{\"Games\":[" +
        "{\"Game\":\"MSFS2024\",\"DisplayName\":\"Microsoft Flight Simulator 2024\",\"Installed\":true,\"Tracked\":true,\"State\":\"unchanged\",\"ConfigPath\":\"C:\\\\c\\\\joypro-input-profiles\"," +
        "\"Watch\":[{\"Directory\":\"C:\\\\w\\\\wgs\",\"Filters\":[\"containers.index\"],\"IncludeSubdirectories\":true}],\"RegistryKey\":\"\",\"ProcessNames\":[\"FlightSimulator2024\"]" + Folders + "}," +
        "{\"Game\":\"XPlane12\",\"Installed\":true,\"Tracked\":false,\"State\":\"untracked\",\"ConfigPath\":\"C:\\\\x\\\\X-Plane Joystick Settings.prf\"}," +
        "{\"Game\":\"IL2Game\",\"Installed\":true,\"Tracked\":false,\"State\":\"missing\",\"ConfigPath\":\"C:\\\\il2\"}," +
        "{\"Game\":\"WarThunder\",\"Installed\":false,\"Tracked\":false,\"State\":\"not-installed\",\"ConfigPath\":\"\"}]}";

    [Fact]
    public void Status_GivesTheWatcherTheStoreFoldersAndSettleTime_AndSaysWhyAGameIsNotWatched()
    {
        SyncStatusEntry msfs = Assert.Single(SyncOutput.ParseStatus(Status)!);
        Assert.Equal(["E:\\XboxGames\\Microsoft Flight Simulator 2024\\Content\\", "C:\\Program Files\\WindowsApps\\Microsoft.Limitless_"], msfs.Spec.ProcessFolderList);
        Assert.Equal(10, msfs.Spec.SettleSeconds);
        Assert.Equal(["FlightSimulator2024"], msfs.Spec.ProcessNames);
        Assert.Equal(["XPlane12: untracked (no export or import recorded yet; C:\\x\\X-Plane Joystick Settings.prf)", "IL2Game: no config found (C:\\il2)"],
            SyncOutput.DescribeUnwatched(Status));
        // An older engine sends neither: no folders, the watcher's own quiet time.
        SyncStatusEntry old = Assert.Single(SyncOutput.ParseStatus(Status.Replace(Folders, string.Empty))!);
        Assert.Empty(old.Spec.ProcessFolderList);
        Assert.Equal(0, old.Spec.SettleSeconds);
        Assert.Equal("12 bind(s) JoyPro manages in the game, 11 in the profile, baseline: yes",
            SyncOutput.DiffCounts("{\"Game\":\"MSFS2024\",\"Baseline\":true,\"FileBinds\":12,\"ProfileBinds\":11,\"Changes\":[]}"));
        Assert.Equal(string.Empty, SyncOutput.DiffCounts("not json"));
    }

    // A game imported for the first time is watched at once (up to 2.2.8 only an export refreshed the watch list).
    [Fact]
    public async Task AnImport_RefreshesTheWatchList_ADryRunOrAFailedRunDoesNot()
    {
        GameWatchChoiceTests.WatchFakeEngine fake = new();
        MainWindowViewModel vm = GameWatchChoiceTests.CreateVm(fake, persist: false);
        vm.GameSyncPost = action => action();
        int StatusCalls()
        {
            lock (fake.Calls)
                return fake.Calls.Count(c => NoviceGuiTests.Seq(c, "sync", "status"));
        }
        try
        {
            await vm.StartGameConfigWatcherAsync();
            int before = StatusCalls();
            vm.OnEngineCommandFinished(["msfs2024", "import", "--replace"], 0);
            for (int i = 0; i < 100 && StatusCalls() == before; i++)
                await Task.Delay(20);
            Assert.Equal(before + 1, StatusCalls());
            vm.OnEngineCommandFinished(["msfs2024", "export", "--dry-run"], 0);
            vm.OnEngineCommandFinished(["msfs2024", "import"], 1);
            await Task.Delay(300);
            Assert.Equal(before + 1, StatusCalls());
        }
        finally
        {
            vm.StopGameConfigWatcher();
        }
    }
}
