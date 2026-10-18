using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using JoyPro.Ffb.Core.Host;
using JoyPro.Ffb.Core.Telemetry;
using JoyPro.Ffb.Core.Telemetry.Recording;
using JoyPro.Gui.Models;
using JoyPro.Gui.ViewModels;
using JoyPro.TelemetrySessions;
using Xunit;

namespace JoyPro.Gui.Tests;

// Settings > Telemetry recording against a fake JoyPro.Ffb and fixture sessions: the switches and the settings file,
// the record process manager (which game, when, not for games force feedback covers), the sessions list, the timeline,
// the exports and delete. No test starts the real process or looks at the PC's game processes.
[SupportedOSPlatform("windows")]
public sealed class TelemetryRecordingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "JoyPro.Gui.Tests.Recording", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
    }

    private string ConfigFile => Path.Combine(_root, "telemetry-recording.json");

    private string Folder => Path.Combine(_root, "Recordings");

    private async Task<(MainWindowViewModel Vm, FakeFfbLauncher Launcher)> CreateAsync()
    {
        Directory.CreateDirectory(_root);
        (MainWindowViewModel vm, _, FakeFfbLauncher launcher) = await FfbTelemetryTests.CreateLoadedVmAsync(UiStage.Settings);
        vm.TelemetryRecordingConfigFile = () => ConfigFile;
        vm.TelemetryRecordingPollInterval = TimeSpan.FromHours(1);
        vm.ApplyTelemetryRecordingConfig(new TelemetryRecordingConfig { Folder = Folder });
        return (vm, launcher);
    }

    internal static string WriteSession(string folder, string game, string aircraft, DateTime created, int seconds = 60, bool position = true)
    {
        Directory.CreateDirectory(folder);
        string path = TelemetrySessionStore.NewSessionPath(folder, game, created.ToLocalTime());
        Dictionary<string, string> header = new()
        {
            ["game"] = game,
            ["gameTitle"] = TelemetryRecordingGames.Find(game)?.Title ?? game,
            ["created"] = TelemetrySessionFormat.FormatTime(created),
            ["info.aircraft"] = aircraft,
        };
        if (position)
        {
            header["nav.lat"] = "x.lat*1+0";
            header["nav.lon"] = "x.lon*1+0";
            header["nav.alt"] = "x.alt*1+0";
            header["nav.ias"] = "x.ias*1+0";
            header["nav.pitch"] = "x.pitch*1+0";
        }
        using TelemetrySessionWriter writer = new(path, header);
        writer.Channel("x.alt", "m", "Altitude", "Position", 0, 10000);
        writer.Channel("x.ias", "m/s", "Indicated airspeed", "Flight", 0, 300);
        writer.Channel("x.pitch", "deg", "Pitch", "Attitude", -90, 90);
        for (int i = 0; i <= seconds * 10; i++)
        {
            double t = i / 10.0;
            writer.Frame(t, new Dictionary<string, double>
            {
                ["x.lat"] = 41.6 + t * 1e-4,
                ["x.lon"] = 41.6,
                ["x.alt"] = 1000 + 500 * Math.Sin(t / 10),
                ["x.ias"] = 120 + t,
                ["x.pitch"] = 10 * Math.Cos(t / 7)
            });
        }
        writer.Event(seconds / 2.0, "message", "Splash one");
        writer.Finish("mission ended", created.AddSeconds(seconds));
        return path;
    }

    [Fact]
    public async Task Off_ByDefault_NothingPolledNothingWritten()
    {
        (MainWindowViewModel vm, FakeFfbLauncher launcher) = await CreateAsync();
        Assert.False(vm.TelemetryRecordingEnabled);
        Assert.False(vm.TelemetryRecordingPolling);
        vm.PollTelemetryRecordingGamesOnce();
        Assert.Empty(launcher.Started);
        Assert.False(File.Exists(ConfigFile));
        Assert.Equal(10, vm.TelemetryRecordingGames.Count);
        Assert.All(vm.TelemetryRecordingGames, g => Assert.True(g.Enabled));
        Assert.Equal(20, vm.SelectedTelemetrySampleRate?.Value);
        Assert.Contains("Off", vm.TelemetryRecordingStateText);
        Assert.NotNull(vm.FindSettingsSection("telemetry-recording"));
    }

    [Fact]
    public async Task Switches_AreSavedToTheSettingsFile()
    {
        (MainWindowViewModel vm, _) = await CreateAsync();
        vm.TelemetryRecordingEnabled = true;
        Assert.True(vm.TelemetryRecordingPolling);
        vm.TelemetryRecordingGames.Single(g => g.Id == "IL2Game").Enabled = false;
        vm.SelectedTelemetrySampleRate = vm.TelemetrySampleRates.Single(r => r.Value == 50);
        vm.SelectedTelemetrySizeCap = vm.TelemetrySizeCaps.Single(r => r.Value == 10240);
        vm.SelectedTelemetryKeepDays = vm.TelemetryKeepDaysChoices.Single(r => r.Value == 30);
        vm.TelemetryRecordingKeepRaw = false;
        string other = Path.Combine(_root, "Elsewhere");
        vm.SetTelemetryRecordingFolder(other);

        TelemetryRecordingConfig saved = TelemetryRecordingConfig.Load(ConfigFile);
        Assert.True(saved.Enabled);
        Assert.False(saved.Records("IL2Game"));
        Assert.True(saved.Records("DCS"));
        Assert.Equal(50, saved.SampleHz);
        Assert.Equal(10240, saved.MaxTotalMb);
        Assert.Equal(30, saved.KeepDays);
        Assert.False(saved.KeepRaw);
        Assert.Equal(other, saved.Folder);
        Assert.Equal(other, vm.TelemetryRecordingFolderText);
        vm.UseDefaultTelemetryFolderCommand.Execute(null);
        Assert.Equal("", TelemetryRecordingConfig.Load(ConfigFile).Folder);
        Assert.True(vm.TelemetryRecordingUsesDefaultFolder);

        vm.TelemetryRecordingEnabled = false;
        Assert.False(vm.TelemetryRecordingPolling);
        Assert.False(TelemetryRecordingConfig.Load(ConfigFile).Enabled);
    }

    [Fact]
    public async Task ProcessManager_RecordsARunningGame_AndLeavesForceFeedbackGamesToJoyProFfbRun()
    {
        (MainWindowViewModel vm, FakeFfbLauncher launcher) = await CreateAsync();
        vm.TelemetryRecordingEnabled = true;
        launcher.RunningGames.Add("X-Plane");
        vm.PollTelemetryRecordingGamesOnce();
        FakeFfbHostProcess process = Assert.Single(launcher.Started);
        Assert.Equal(["record", "--game", "XPlane12", "--game-pid", "4242", "--parent-pid", "777", "--protocol"], process.Arguments);
        Assert.Equal("XPlane12", vm.TelemetryRecordHostGame);
        vm.PollTelemetryRecordingGamesOnce();
        Assert.Single(launcher.Started);                                 // one process at a time

        process.Emit(HostProtocol.RecordingLine("recording", "recording", "XPlane12", @"E:\Rec\2026-10-18_14-03-22_XPlane12.jptel", 1234, 0, null, "Cessna 172"));
        Assert.Contains("Recording X-Plane 12 - Cessna 172: 2026-10-18_14-03-22_XPlane12.jptel", vm.TelemetryRecordingStateText);

        // A settings change reaches the process.
        vm.TelemetryRecordingKeepRaw = false;
        Assert.Contains(HostProtocol.Reload, process.Sent);

        // The game ends: the process ends; the same game process is not recorded again.
        process.Exit();
        Assert.Equal("", vm.TelemetryRecordHostGame);
        vm.PollTelemetryRecordingGamesOnce();
        Assert.Single(launcher.Started);

        // DCS with telemetry force feedback on: JoyPro.Ffb run records it, no second process for the same port.
        launcher.RunningGames.Clear();
        launcher.RunningGames.Add("DCS");
        launcher.GamePid = 5000;
        vm.FfbTelemetryEnabled = true;
        Assert.True(vm.FfbCoversRecordingGame("DCS"));
        vm.PollTelemetryRecordingGamesOnce();
        Assert.DoesNotContain(launcher.Started, p => p.Arguments.Length > 0 && p.Arguments[0] == "record" && p.Arguments.Contains("DCS"));

        // Force feedback ended at once for this game process (no device): the game is recorded without it.
        FakeFfbHostProcess run = launcher.Started.Last(p => p.Arguments[0] == "run");
        run.Emit("{\"type\":\"exit\",\"reason\":\"no device\"}");
        run.Exit();
        Assert.False(vm.FfbCoversRecordingGame("DCS", 5000));
        vm.PollTelemetryRecordingGamesOnce();
        FakeFfbHostProcess dcsRecord = launcher.Started.Last();
        Assert.Equal(["record", "--game", "DCS", "--game-pid", "5000", "--parent-pid", "777", "--protocol"], dcsRecord.Arguments);
        dcsRecord.Exit();

        // Recording switched off: the process gets "stop".
        vm.FfbTelemetryEnabled = false;
        launcher.RunningGames.Add("FlightSimulator2024");
        launcher.GamePid = 6000;
        vm.PollTelemetryRecordingGamesOnce();
        FakeFfbHostProcess second = launcher.Started.Last(p => p.Arguments[0] == "record");
        vm.TelemetryRecordingEnabled = false;
        await Task.Delay(50);
        Assert.Contains(HostProtocol.Stop, second.Sent);
    }

    [Fact]
    public async Task ForceFeedbackProcess_RecordingLines_ShowInTheSection()
    {
        (MainWindowViewModel vm, _) = await CreateAsync();
        vm.TelemetryRecordingEnabled = true;
        vm.OnTelemetryRecordingLine(System.Text.Json.Nodes.JsonNode.Parse(HostProtocol.RecordingLine("recording", "recording", "DCS", @"C:\x\a.jptel", 10, 0, null, "F-16C_50"))!.AsObject());
        Assert.StartsWith("Recording DCS World - F-16C_50: a.jptel", vm.TelemetryRecordingStateText);
        vm.OnTelemetryRecordingLine(System.Text.Json.Nodes.JsonNode.Parse(HostProtocol.RecordingLine("listening", "listening", "DCS", null, 0, 1, null))!.AsObject());
        Assert.Contains("waiting for its telemetry", vm.TelemetryRecordingStateText);
    }

    [Fact]
    public async Task SessionsList_Preview_Exports_AndDelete()
    {
        (MainWindowViewModel vm, _) = await CreateAsync();
        string older = WriteSession(Folder, "DCS", "F-16C_50", new DateTime(2026, 10, 17, 18, 0, 0, DateTimeKind.Utc));
        string newer = WriteSession(Folder, "IL2Game", "Bf 109 G-14", new DateTime(2026, 10, 18, 9, 30, 0, DateTimeKind.Utc), position: false);
        await vm.RefreshTelemetrySessionsAsync();
        Assert.Equal([newer, older], vm.TelemetrySessions.Select(s => s.Path));
        Assert.StartsWith("2 recordings", vm.TelemetrySessionsSummary);
        TelemetrySessionItem il2 = vm.TelemetrySessions[0];
        Assert.Equal("IL-2 Great Battles", il2.Game);
        Assert.Equal("Bf 109 G-14", il2.Aircraft);
        Assert.Equal("1:00", il2.Duration);
        Assert.False(il2.CanExportAcmi);                                 // no position in IL-2's telemetry
        Assert.Same(il2, vm.SelectedTelemetrySession);
        Assert.False(vm.CanExportSelectedTelemetryAcmi);

        vm.SelectedTelemetrySession = vm.TelemetrySessions[1];
        await vm.LoadTelemetryPreviewAsync(vm.SelectedTelemetrySession);
        Assert.True(vm.CanExportSelectedTelemetryAcmi);
        Assert.NotNull(vm.TelemetryPreviewTracks);
        Assert.Equal(3, vm.TelemetryPreviewTracks!.Count);
        Assert.StartsWith("Altitude [m]", vm.TelemetryPreviewTracks[0].Label);
        Assert.InRange(vm.TelemetryPreviewTracks[0].Max, 1490, 1500);

        string? acmi = await vm.ExportSelectedTelemetryAsync("acmi-zip");
        Assert.NotNull(acmi);
        Assert.EndsWith(".zip.acmi", acmi);
        using (ZipArchive zip = ZipFile.OpenRead(acmi!))
            Assert.Single(zip.Entries);
        Assert.StartsWith("Exported for Tacview", vm.TelemetryRecordingMessage);
        string? csv = await vm.ExportSelectedTelemetryAsync("csv");
        Assert.True(Directory.Exists(csv));
        Assert.True(File.Exists(Path.Combine(csv!, Path.GetFileNameWithoutExtension(older) + ".csv")));

        await vm.DeleteTelemetrySessionAsync();
        Assert.False(File.Exists(older));
        Assert.Contains(vm.AskedConfirmations, c => c.Title == "Delete recording" && c.IsDanger);
        Assert.Single(vm.TelemetrySessions);
    }
}
