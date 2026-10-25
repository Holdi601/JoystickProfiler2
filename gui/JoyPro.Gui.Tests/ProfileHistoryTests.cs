using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using JoyPro.History;
using Xunit;
using Xunit.Abstractions;

namespace JoyPro.Gui.Tests;

// Autosave, the profile history and the last profile at start (docs/profile-history.md): the debounce, the flush when
// closing / switching / before an export, atomic writes, versions and what changed, undo / redo / restore (also of a
// change made outside JoyPro), the retention with simulated timestamps, the size caps, kept versions, a profile of the
// user's size (~450 relations, 3 MB) and the view model's parts. Every history lives in a temp folder of its own.
[SupportedOSPlatform("windows")]
public sealed class ProfileHistoryTests : IDisposable
{
    private readonly ITestOutputHelper output;
    private readonly string root;
    private readonly Func<DateTime> originalClock;
    private readonly string? originalRoot;
    private readonly TimeSpan originalDelay;
    private DateTime now = new(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc);

    public ProfileHistoryTests(ITestOutputHelper output)
    {
        this.output = output;
        root = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "history-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        originalClock = ProfileHistory.UtcNow;
        originalRoot = ProfileHistory.RootOverride;
        originalDelay = MainWindowViewModel.AutosaveDelay;
        ProfileHistory.RootOverride = Path.Combine(root, "ProfileHistory");
        ProfileHistory.UtcNow = () => now;
    }

    public void Dispose()
    {
        ProfileHistory.UtcNow = originalClock;
        ProfileHistory.RootOverride = originalRoot;
        MainWindowViewModel.AutosaveDelay = originalDelay;
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (Exception)
        {
            // A temp folder left behind is harmless.
        }
    }

    // ---- Autosave scheduler ----------------------------------------------------------------------------------------

    // Hands out the debounce waits; the test decides when one is over.
    private sealed class ManualDelay
    {
        public List<(TaskCompletionSource Done, CancellationToken Token)> Waits { get; } = [];

        public Task Wait(TimeSpan delay, CancellationToken token)
        {
            TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);
            token.Register(() => done.TrySetCanceled());
            Waits.Add((done, token));
            return done.Task;
        }

        public void ElapseAll()
        {
            foreach ((TaskCompletionSource done, _) in Waits.ToList())
                done.TrySetResult();
        }
    }

    [Fact]
    public async Task Autosave_Debounce_SavesOnceAfterTheChangesStop()
    {
        ManualDelay delay = new();
        int saves = 0;
        AutosaveScheduler scheduler = new(_ => { saves++; return Task.CompletedTask; }, TimeSpan.FromSeconds(2), delay.Wait);

        scheduler.Schedule();
        scheduler.Schedule();
        scheduler.Schedule();
        Assert.True(scheduler.IsPending);
        Assert.Equal(3, delay.Waits.Count);
        // The first two waits were replaced by the next change.
        Assert.True(delay.Waits[0].Token.IsCancellationRequested);
        Assert.True(delay.Waits[1].Token.IsCancellationRequested);
        Assert.Equal(0, saves);

        delay.ElapseAll();
        await WaitUntilAsync(() => saves == 1 && !scheduler.IsSaving);
        await Task.Delay(50);
        Assert.Equal(1, saves);
        Assert.False(scheduler.IsPending);
        Assert.NotNull(scheduler.LastSavedUtc);
    }

    [Fact]
    public async Task Autosave_Flush_SavesAtOnce_AndTheTimerDoesNotSaveAgain()
    {
        ManualDelay delay = new();
        int saves = 0;
        AutosaveScheduler scheduler = new(_ => { saves++; return Task.CompletedTask; }, TimeSpan.FromSeconds(2), delay.Wait);

        await scheduler.FlushAsync();
        Assert.Equal(0, saves);

        scheduler.Schedule();
        await scheduler.FlushAsync();
        Assert.Equal(1, saves);
        Assert.False(scheduler.IsPending);

        delay.ElapseAll();
        await Task.Delay(50);
        Assert.Equal(1, saves);
    }

    [Fact]
    public async Task Autosave_ChangeDuringASave_IsSavedRightAfterIt()
    {
        ManualDelay delay = new();
        TaskCompletionSource firstSave = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int saves = 0;
        AutosaveScheduler scheduler = new(async _ =>
        {
            saves++;
            if (saves == 1)
                await firstSave.Task;
        }, TimeSpan.FromSeconds(2), delay.Wait);

        scheduler.Schedule();
        Task flush = scheduler.FlushAsync();
        Assert.True(scheduler.IsSaving);
        scheduler.Schedule();
        firstSave.SetResult();
        await flush;
        await scheduler.FlushAsync();
        Assert.Equal(2, saves);
        Assert.False(scheduler.IsPending);
    }

    [Fact]
    public async Task Autosave_FailedSave_StaysPending()
    {
        bool fail = true;
        int saves = 0;
        AutosaveScheduler scheduler = new(_ =>
        {
            saves++;
            if (fail)
                throw new IOException("disk full");
            return Task.CompletedTask;
        }, TimeSpan.FromSeconds(2), new ManualDelay().Wait);

        scheduler.Schedule();
        await scheduler.FlushAsync();
        Assert.True(scheduler.IsPending);
        Assert.Equal("disk full", scheduler.LastError?.Message);

        fail = false;
        await scheduler.FlushAsync();
        Assert.False(scheduler.IsPending);
        Assert.Null(scheduler.LastError);
        Assert.Equal(2, saves);
    }

    // ---- Atomic writes ---------------------------------------------------------------------------------------------

    [Fact]
    public void WriteFileAtomic_ReplacesTheWholeFile_AndCleansUpWhatACrashLeft()
    {
        string profile = Path.Combine(root, "atomic.pr0file");
        File.WriteAllText(profile, "old content that is longer than the new one");
        // A crash during an earlier save left its temp file behind.
        string stale = profile + ".tmp-1234-deadbeef";
        File.WriteAllText(stale, "half written");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddMinutes(-30));

        ProfileHistory.WriteFileAtomic(profile, Encoding.UTF8.GetBytes("new"));

        Assert.Equal("new", File.ReadAllText(profile));
        Assert.False(File.Exists(stale));
        Assert.Empty(Directory.GetFiles(root, "atomic.pr0file.tmp-*"));
    }

    [Fact]
    public void WriteFileAtomic_WhenTheFileCannotBeReplaced_KeepsTheOldFileWhole()
    {
        string profile = Path.Combine(root, "locked.pr0file");
        File.WriteAllText(profile, "the user's profile");
        using (FileStream holder = new(profile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.ThrowsAny<IOException>(() => ProfileHistory.WriteFileAtomic(profile, Encoding.UTF8.GetBytes("would be lost")));
        }
        Assert.Equal("the user's profile", File.ReadAllText(profile));
        Assert.Empty(Directory.GetFiles(root, "locked.pr0file.tmp-*"));
    }

    // ---- Versions, what changed, undo / redo / restore ---------------------------------------------------------------

    [Fact]
    public void Versions_AreRecorded_WithWhatChanged()
    {
        string profile = NewProfile("described");
        Save(profile, "v1", Items(("Pitch", 3)), "New profile");
        now = now.AddSeconds(10);
        Save(profile, "v2", Items(("Pitch", 3), ("Throttle ALL", 42)), "Adding relation");
        now = now.AddSeconds(10);
        Save(profile, "v3", Items(("Pitch", 5), ("Throttle ALL", 42)), "Adding actions");
        now = now.AddSeconds(10);
        // The same content again: no new version.
        Save(profile, "v3", Items(("Pitch", 5), ("Throttle ALL", 42)), "Saving profile");

        ProfileHistoryLog log = ProfileHistory.Read(profile);
        Assert.Equal(3, log.Versions.Count);
        Assert.Equal("Relation 'Throttle ALL' created, 42 actions added", log.Versions[1].Summary);
        Assert.Equal("Adding relation", log.Versions[1].Label);
        Assert.Equal("Relation 'Pitch': 2 actions added", log.Versions[2].Summary);
        Assert.Equal(log.Versions[2].Id, log.CursorVersion!.Id);

        List<string>? diff = ProfileHistory.Diff(profile, log.Versions[0].Id, log.Versions[2].Id);
        Assert.NotNull(diff);
        Assert.Contains("Relation 'Throttle ALL' created, 42 actions added", diff!);
        Assert.Contains("Relation 'Pitch': 2 actions added", diff!);
        Assert.True(log.CanUndo);
        Assert.False(log.CanRedo);
    }

    [Fact]
    public void Describe_RenamesBindsModifiersAndSettings()
    {
        List<string> before =
        [
            "R\tGear\t2\tLanding\tn1\tm1",
            "R\tFlaps\t4\t\tn2\tm2",
            "B\tGear\t\tStick JOY_BTN1\ti1\tmod1\tt1",
            "B\tFlaps\t\tStick JOY_BTN2\ti2\tmod1\tt1",
            "M\tShift\t\tStick JOY_BTN5\th1",
            "S\tDevice names\t\t\ts1"
        ];
        List<string> after =
        [
            "R\tLanding gear\t2\tLanding\tn1\tm1",
            "R\tFlaps\t4\tFlight\tn2\tm3",
            "B\tLanding gear\t\tStick JOY_BTN1\ti1\tmod1\tt1",
            "B\tFlaps\t\tStick JOY_BTN3\ti3\tmod2\tt2",
            "M\tAlt\t\tStick JOY_BTN6\th2",
            "S\tDevice names\t\t\ts2"
        ];
        List<string> changes = ProfileHistory.DescribeChanges(before, after);
        Assert.Contains("Relation 'Gear' renamed to 'Landing gear'", changes);
        Assert.Contains("Relation 'Flaps': groups (none) -> Flight", changes);
        Assert.Contains("'Flaps' moved from Stick JOY_BTN2 to Stick JOY_BTN3", changes);
        Assert.Contains("'Flaps': modifiers changed", changes);
        Assert.Contains("'Flaps': axis tuning changed", changes);
        Assert.Contains("Modifier 'Alt' created on Stick JOY_BTN6", changes);
        Assert.Contains("Modifier 'Shift' deleted", changes);
        Assert.Contains("Device names changed", changes);
        Assert.Equal("1 relation renamed, 1 relation change, 1 bind set, 3 bind changes, 2 modifier changes, 1 setting change", ProfileHistory.Summarize(changes));
    }

    [Fact]
    public void UndoRedoRestore_WriteTheVersionsBack_AndAreVersionsThemselves()
    {
        string profile = NewProfile("undo");
        Save(profile, "A", Items(("One", 1)), "First");
        now = now.AddSeconds(5);
        Save(profile, "B", Items(("One", 1), ("Two", 2)), "Second");
        now = now.AddSeconds(5);
        Save(profile, "C", Items(("One", 1), ("Two", 2), ("Three", 3)), "Third");

        now = now.AddSeconds(5);
        ProfileHistoryWriteResult undo = ProfileHistory.Undo(profile);
        Assert.True(undo.Changed);
        Assert.Equal("B", File.ReadAllText(profile));
        Assert.Contains("Third", undo.Message);
        now = now.AddSeconds(5);
        ProfileHistory.Undo(profile);
        Assert.Equal("A", File.ReadAllText(profile));
        now = now.AddSeconds(5);
        Assert.False(ProfileHistory.Undo(profile).Changed);
        Assert.Equal("A", File.ReadAllText(profile));

        now = now.AddSeconds(5);
        Assert.True(ProfileHistory.Redo(profile).Changed);
        Assert.Equal("B", File.ReadAllText(profile));
        ProfileHistoryLog log = ProfileHistory.Read(profile);
        Assert.True(log.CanRedo);
        Assert.Equal("Third", log.RedoTarget!.Label);
        // Every undo and redo is a version too (content shared: no extra space).
        Assert.Equal(6, log.Versions.Count);
        Assert.Equal(3, Directory.GetFiles(Path.Combine(log.Folder, "blobs"), "*.items.gz").Length);

        // A new change clears the redo.
        now = now.AddSeconds(5);
        Save(profile, "D", Items(("One", 1), ("Two", 2), ("Four", 4)), "Fourth");
        Assert.False(ProfileHistory.Read(profile).CanRedo);

        // Restore of the first version, and undo of the restore.
        now = now.AddSeconds(5);
        int first = ProfileHistory.Read(profile).Versions[0].Id;
        ProfileHistoryWriteResult restore = ProfileHistory.Restore(profile, first);
        Assert.True(restore.Changed);
        Assert.Equal("A", File.ReadAllText(profile));
        Assert.Equal(ProfileVersionKind.Restore, ProfileHistory.Read(profile).Latest!.Kind);
        now = now.AddSeconds(5);
        ProfileHistory.Undo(profile);
        Assert.Equal("D", File.ReadAllText(profile));
    }

    [Fact]
    public void Undo_AfterAChangeOutsideJoyPro_KeepsThatChangeAsAVersion()
    {
        string profile = NewProfile("outside");
        Save(profile, "A", Items(("One", 1)), "First");
        now = now.AddSeconds(5);
        Save(profile, "B", Items(("One", 1), ("Two", 2)), "Second");
        // Another program (or a copy from a backup) wrote the file.
        File.WriteAllText(profile, "X");

        now = now.AddSeconds(5);
        ProfileHistoryWriteResult undo = ProfileHistory.Undo(profile);
        Assert.True(undo.Changed);
        Assert.Equal("B", File.ReadAllText(profile));
        ProfileHistoryLog log = ProfileHistory.Read(profile);
        Assert.Contains(log.Versions, v => v.Kind == ProfileVersionKind.External);
        // Redo brings the outside change back: nothing was lost.
        now = now.AddSeconds(5);
        ProfileHistory.Redo(profile);
        Assert.Equal("X", File.ReadAllText(profile));
    }

    [Fact]
    public void SavesWithinTwoSeconds_AreOneVersion()
    {
        string profile = NewProfile("burst");
        Save(profile, "A", Items(("One", 1)), "First");
        now = now.AddSeconds(10);
        Save(profile, "B", Items(("One", 1), ("Two", 2)), "Adding relation");
        now = now.AddSeconds(1);
        Save(profile, "C", Items(("One", 1), ("Two", 2), ("Three", 3)), "Setting bind");

        ProfileHistoryLog log = ProfileHistory.Read(profile);
        Assert.Equal(2, log.Versions.Count);
        Assert.Equal("Adding relation + Setting bind", log.Latest!.Label);
        Assert.Equal("Relation 'Two' created, 2 actions added; Relation 'Three' created, 3 actions added", log.Latest.Summary);
        now = now.AddSeconds(5);
        ProfileHistory.Undo(profile);
        Assert.Equal("A", File.ReadAllText(profile));
    }

    [Fact]
    public void Checkpoint_BeforeAnExport_IsKept_AndDoesNotMoveUndo()
    {
        string profile = NewProfile("checkpoint");
        Save(profile, "A", Items(("One", 1)), "First");
        now = now.AddSeconds(10);
        Save(profile, "B", Items(("One", 1), ("Two", 2)), "Second");
        now = now.AddSeconds(10);
        ProfileVersion? checkpoint = ProfileHistory.Checkpoint(profile, "Before export to DCS");
        Assert.NotNull(checkpoint);
        ProfileHistoryLog log = ProfileHistory.Read(profile);
        Assert.Equal("Before export to DCS", log.Latest!.Label);
        Assert.Equal(ProfileVersionKind.Checkpoint, log.Latest.Kind);
        Assert.Equal("Second", log.UndoSubject!.Label);
        now = now.AddSeconds(5);
        ProfileHistory.Undo(profile);
        Assert.Equal("A", File.ReadAllText(profile));
    }

    [Fact]
    public void RenamedProfile_TakesItsHistoryAlong_AndAMovedFileIsFoundByItsContent()
    {
        string profile = NewProfile("before-rename");
        Save(profile, "A", Items(("One", 1)), "First");
        string renamed = Path.Combine(root, "after-rename.pr0file");
        File.Move(profile, renamed);
        Assert.True(ProfileHistory.Move(profile, renamed));
        Assert.Single(ProfileHistory.Read(renamed).Versions);
        Assert.Empty(ProfileHistory.Read(profile).Versions);

        // Moved in Explorer: the next save finds the history by the file's content.
        string moved = Path.Combine(root, "moved", "elsewhere.pr0file");
        Directory.CreateDirectory(Path.GetDirectoryName(moved)!);
        File.Move(renamed, moved);
        now = now.AddSeconds(10);
        byte[] next = Encoding.UTF8.GetBytes("B");
        string previousHash = ProfileHistory.HashOf(File.ReadAllBytes(moved));
        File.WriteAllBytes(moved, next);
        ProfileHistory.Record(moved, next, new ProfileHistoryRecordOptions { Label = "Second", Items = () => Items(("One", 1), ("Two", 2)), PreviousHash = previousHash });
        Assert.Equal(2, ProfileHistory.Read(moved).Versions.Count);
    }

    // ---- Retention, caps, kept versions -------------------------------------------------------------------------------

    [Fact]
    public void Prune_KeepsEverythingOfTheLastDay_ThenHourlyDailyWeekly()
    {
        string profile = NewProfile("retention");
        DateTime start = now;
        List<DateTime> times = [];
        // 100 days ago: 6 versions over two weeks (Monday 2026-07-06 ... ) -> one per week.
        DateTime weekStart = new(2026, 6, 29, 9, 0, 0, DateTimeKind.Utc); // a Monday
        for (int i = 0; i < 3; i++)
            times.Add(weekStart.AddDays(i));
        for (int i = 0; i < 3; i++)
            times.Add(weekStart.AddDays(7 + i));
        // 30 days ago: 4 versions on one day, 2 on the next -> one per day.
        DateTime day = start.AddDays(-30).Date.AddHours(8);
        for (int i = 0; i < 4; i++)
            times.Add(day.AddHours(i));
        times.Add(day.AddDays(1));
        times.Add(day.AddDays(1).AddHours(2));
        // 3 days ago: 5 versions in one hour, 2 in the next -> one per hour.
        DateTime hour = start.AddDays(-3).Date.AddHours(10);
        for (int i = 0; i < 5; i++)
            times.Add(hour.AddMinutes(i * 10));
        times.Add(hour.AddHours(1).AddMinutes(5));
        times.Add(hour.AddHours(1).AddMinutes(20));
        // The last day: 6 versions, all stay.
        for (int i = 0; i < 6; i++)
            times.Add(start.AddHours(-20 + i));

        int n = 0;
        foreach (DateTime t in times.OrderBy(t => t))
        {
            now = t;
            Save(profile, "content " + n, Items(("R", n)), "Change " + n, NoAutoPrune);
            n++;
        }
        // One old version is kept by the user.
        ProfileHistoryLog before = ProfileHistory.Read(profile);
        int kept = before.Versions.First(v => v.Utc == weekStart.AddDays(1)).Id;
        Assert.True(ProfileHistory.SetKeep(profile, kept, true));

        now = start;
        ProfileHistoryPruneResult dry = ProfileHistory.Prune(profile, dryRun: true);
        Assert.Equal(times.Count, ProfileHistory.Read(profile).Versions.Count);

        ProfileHistoryPruneResult result = ProfileHistory.Prune(profile);
        Assert.Equal(dry.Removed.Count, result.Removed.Count);
        ProfileHistoryLog after = ProfileHistory.Read(profile);
        // weekly: 2 (+ the kept one), daily: 2, hourly: 2, last day: 6.
        Assert.Equal(2 + 1 + 2 + 2 + 6, after.Versions.Count);
        Assert.Contains(after.Versions, v => v.Id == kept);
        Assert.Equal(6, after.Versions.Count(v => (start - v.Utc).TotalHours < 24));
        // Blobs of removed versions are gone, the others readable.
        foreach (ProfileVersion version in after.Versions)
            Assert.NotEmpty(ProfileHistory.ReadContent(profile, version.Id));
        Assert.Equal(after.Versions.Count, Directory.GetFiles(Path.Combine(after.Folder, "blobs"), "*.gz").Count(f => !f.EndsWith(".items.gz", StringComparison.Ordinal)));
        // Undo skips what was cleaned up.
        Assert.True(after.CanUndo);
    }

    [Fact]
    public void Prune_SizeCap_RemovesTheOldestFirst_ButNeverKeptOrTheNewest()
    {
        string profile = NewProfile("cap");
        Random random = new(7);
        List<int> ids = [];
        for (int i = 0; i < 12; i++)
        {
            now = now.AddMinutes(1);
            byte[] noise = new byte[20 * 1024];
            random.NextBytes(noise);
            ids.Add(SaveBytes(profile, noise, Items(("R", i)), "Change " + i).Id);
        }
        Assert.True(ProfileHistory.SetKeep(profile, ids[0], true));
        long before = ProfileHistory.Usage(profile).Bytes;
        Assert.True(before > 12 * 20 * 1024);

        ProfileHistoryPolicy policy = new() { ProfileCapBytes = 100 * 1024 };
        ProfileHistoryPruneResult result = ProfileHistory.Prune(profile, policy);
        ProfileHistoryLog after = ProfileHistory.Read(profile);
        Assert.True(result.BytesAfter <= policy.ProfileCapBytes, "after " + result.BytesAfter);
        Assert.True(ProfileHistory.Usage(profile).Bytes <= policy.ProfileCapBytes + 4096);
        Assert.Contains(after.Versions, v => v.Id == ids[0]);
        Assert.Contains(after.Versions, v => v.Id == ids[^1]);
        // The oldest unkept versions went first.
        Assert.DoesNotContain(after.Versions, v => v.Id == ids[1]);
        Assert.Contains(after.Versions, v => v.Id == ids[^2]);
    }

    [Fact]
    public void PruneAll_GlobalCap_TakesTheOldestOfAllProfiles()
    {
        string first = NewProfile("global-a");
        string second = NewProfile("global-b");
        Random random = new(3);
        for (int i = 0; i < 6; i++)
        {
            foreach (string profile in new[] { first, second })
            {
                now = now.AddMinutes(1);
                byte[] noise = new byte[20 * 1024];
                random.NextBytes(noise);
                SaveBytes(profile, noise, Items(("R", i)), "Change " + i);
            }
        }
        ProfileHistoryPolicy policy = new() { GlobalCapBytes = 140 * 1024 };
        ProfileHistory.PruneAll(policy);
        ProfileHistoryUsage usage = ProfileHistory.UsageAll();
        Assert.Equal(2, usage.Profiles);
        Assert.True(usage.Bytes <= policy.GlobalCapBytes, "usage " + usage.Bytes);
        // Both profiles keep their newest version.
        Assert.NotNull(ProfileHistory.Read(first).Latest);
        Assert.NotNull(ProfileHistory.Read(second).Latest);
        DateTime oldestLeft = ProfileHistory.Read(first).Versions.Concat(ProfileHistory.Read(second).Versions).Min(v => v.Utc);
        Assert.True(oldestLeft > now.AddMinutes(-12));
    }

    // ---- A profile of the user's size ---------------------------------------------------------------------------------

    [Fact]
    public void LargeProfile_450Relations_RecordsListsDiffsAndUndoesQuickly()
    {
        string profile = NewProfile("large");
        Random random = new(11);
        // ~3 MB like the user's profile: repetitive (BinaryFormatter strings) with some noise.
        StringBuilder text = new();
        for (int r = 0; r < 450; r++)
        {
            text.Append("Relation ").Append(r).Append(' ');
            for (int a = 0; a < 120; a++)
                text.Append("DCS|Input.Generic.").Append(random.Next(0, 5000)).Append("|FA-18C_hornet+,F-16C_50+,A-10C_2+;");
        }
        byte[] content = Encoding.UTF8.GetBytes(text.ToString());
        output.WriteLine("profile size " + content.Length);
        Assert.True(content.Length > 2_500_000);
        List<(string, int)> relations = Enumerable.Range(0, 450).Select(i => ("Relation " + i, 40 + i % 30)).ToList();

        Stopwatch watch = Stopwatch.StartNew();
        long slowest = 0;
        for (int i = 0; i < 15; i++)
        {
            now = now.AddSeconds(10);
            content[random.Next(content.Length)] = (byte)'x';
            relations[i] = (relations[i].Item1, relations[i].Item2 + 1);
            Stopwatch one = Stopwatch.StartNew();
            SaveBytes(profile, content, Items(relations.ToArray()), "Change " + i);
            slowest = Math.Max(slowest, one.ElapsedMilliseconds);
        }
        long total = watch.ElapsedMilliseconds;
        output.WriteLine("15 saves " + total + " ms, slowest " + slowest + " ms, history " + ProfileHistory.Usage(profile).Bytes + " bytes");
        // The profile file is flushed to the disk on every save (a busy disk takes up to a second for that).
        Assert.True(total / 15 < 1500, "a save took " + total / 15 + " ms on average");
        Assert.True(slowest < 5000, "one save took " + slowest + " ms");

        watch.Restart();
        ProfileHistoryLog log = ProfileHistory.Read(profile);
        long readMs = watch.ElapsedMilliseconds;
        watch.Restart();
        List<string>? diff = ProfileHistory.Diff(profile, log.Versions[0].Id, log.Latest!.Id);
        long diffMs = watch.ElapsedMilliseconds;
        watch.Restart();
        ProfileHistoryWriteResult undo = ProfileHistory.Undo(profile);
        long undoMs = watch.ElapsedMilliseconds;
        output.WriteLine("read " + readMs + " ms, diff " + diffMs + " ms, undo " + undoMs + " ms");
        Assert.Equal(15, log.Versions.Count);
        Assert.NotNull(diff);
        Assert.Equal(14, diff!.Count);
        Assert.True(undo.Changed);
        Assert.True(readMs < 500, "read " + readMs);
        Assert.True(diffMs < 1000, "diff " + diffMs);
        Assert.True(undoMs < 3000, "undo " + undoMs);
        // Compressed: far less than 15 full copies.
        Assert.True(ProfileHistory.Usage(profile).Bytes < 15L * content.Length / 2);
    }

    // ---- View model ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AutoOpen_OpensTheLastProfile_AfterTheWindowShows()
    {
        HistoryFakeEngine fake = new();
        MainWindowViewModel vm = CreateVm(fake);
        string profile = NewProfile("auto-open");
        File.WriteAllText(profile, "profile");
        vm.LastProfilePath = profile;
        Assert.True(vm.LoadLastProfileOnStartup);
        Assert.False(vm.HasProfile);

        await vm.AutoOpenLastProfileOnStartupAsync();

        Assert.Equal(profile, vm.ProfilePath);
        Assert.Contains(fake.Calls, c => NoviceGuiTests.Seq(c, "profile", "load", profile));
        // The Start page stays in front (opening a profile by hand shows the Relations page).
        Assert.Equal(UiStage.Start, vm.CurrentStage);
    }

    [Fact]
    public async Task AutoOpen_MissingFile_LeavesNoProfileOpen()
    {
        HistoryFakeEngine fake = new();
        MainWindowViewModel vm = CreateVm(fake);
        vm.LastProfilePath = Path.Combine(root, "gone.pr0file");

        await vm.AutoOpenLastProfileOnStartupAsync();

        Assert.False(vm.HasProfile);
        Assert.Equal("No profile open", vm.ProfileDisplayName);
        Assert.Contains("no longer there", vm.StatusText);
        Assert.DoesNotContain(fake.Calls, c => NoviceGuiTests.Seq(c, "profile", "load"));
    }

    [Fact]
    public async Task AutoOpen_SwitchedOff_OpensNothing()
    {
        HistoryFakeEngine fake = new();
        MainWindowViewModel vm = CreateVm(fake);
        string profile = NewProfile("auto-off");
        File.WriteAllText(profile, "profile");
        vm.LastProfilePath = profile;
        vm.LoadLastProfileOnStartup = false;

        await vm.AutoOpenLastProfileOnStartupAsync();

        Assert.False(vm.HasProfile);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task EngineChange_PassesItsName_AndTheTopBarSaysSaved()
    {
        HistoryFakeEngine fake = new();
        MainWindowViewModel vm = CreateVm(fake);
        string profile = NewProfile("saved-state");
        File.WriteAllText(profile, "profile");
        vm.ProfilePath = profile;
        vm.RelationNameInput = "Gear";
        vm.RelationFromInput = "DCS:gear";

        await vm.SaveProfileCommand.ExecuteAsync(null);

        IReadOnlyList<string> call = fake.Calls.Last(c => NoviceGuiTests.Seq(c, "profile", "save"));
        int note = call.ToList().IndexOf("--history-note");
        Assert.True(note >= 0);
        Assert.Equal("Saving profile", call[note + 1]);
        Assert.True(vm.IsProfileSaved);
        Assert.Equal("Saved ✓", vm.SaveStateText);
        Assert.False(vm.IsProfileSaving);
    }

    [Fact]
    public async Task UndoAndRedo_InTheViewModel_UseTheHistory()
    {
        HistoryFakeEngine fake = new();
        MainWindowViewModel vm = CreateVm(fake);
        string profile = NewProfile("vm-undo");
        Save(profile, "A", Items(("One", 1)), "First");
        now = now.AddSeconds(5);
        Save(profile, "B", Items(("One", 1), ("Two", 2)), "Adding relation");
        vm.ProfilePath = profile;

        Assert.True(vm.CanUndo);
        Assert.Equal("Undo: Adding relation: Relation 'Two' created, 2 actions added", vm.UndoLabel);
        Assert.False(vm.CanRedo);

        now = now.AddSeconds(5);
        await vm.UndoLastChangeCommand.ExecuteAsync(null);
        Assert.Equal("A", File.ReadAllText(profile));
        Assert.True(vm.CanRedo);
        Assert.Contains("Undone", vm.StatusText);
        // The pages are read again from the restored file.
        Assert.Contains(fake.Calls, c => NoviceGuiTests.Seq(c, "relations", "list"));

        now = now.AddSeconds(5);
        await vm.RedoLastChangeCommand.ExecuteAsync(null);
        Assert.Equal("B", File.ReadAllText(profile));
        Assert.False(vm.CanRedo);
    }

    [Fact]
    public async Task HistoryPanel_ListsComparesKeepsRestores()
    {
        HistoryFakeEngine fake = new();
        MainWindowViewModel vm = CreateVm(fake);
        string profile = NewProfile("panel");
        Save(profile, "A", Items(("One", 1)), "First");
        now = now.AddSeconds(5);
        Save(profile, "B", Items(("One", 1), ("Throttle ALL", 42)), "Adding relation");
        vm.ProfilePath = profile;

        vm.IsHistoryPanelOpen = true;
        Assert.Equal(2, vm.HistoryVersions.Count);
        HistoryVersionItem newest = vm.HistoryVersions[0];
        HistoryVersionItem oldest = vm.HistoryVersions[1];
        Assert.True(newest.IsCurrent);
        Assert.Equal("Relation 'Throttle ALL' created, 42 actions added", newest.Summary);
        Assert.Contains("This profile: 2 versions", vm.HistoryUsageText);

        vm.ShowHistoryVersionChangesCommand.Execute(newest);
        Assert.Contains("Relation 'Throttle ALL' created, 42 actions added", vm.HistoryCompareLines);
        vm.CompareHistoryVersionCommand.Execute(oldest);
        Assert.True(vm.HasHistoryCompare);
        Assert.Contains("Relation 'Throttle ALL' created, 42 actions added", vm.HistoryCompareLines);

        vm.ToggleHistoryKeepCommand.Execute(oldest);
        Assert.True(oldest.Keep);
        Assert.True(ProfileHistory.Read(profile).Versions[0].Keep);

        now = now.AddSeconds(5);
        await vm.RestoreHistoryVersionCommand.ExecuteAsync(oldest);
        Assert.Equal("A", File.ReadAllText(profile));
        Assert.Equal(3, vm.HistoryVersions.Count);
        Assert.Contains("Restored", vm.HistoryVersions[0].Title);

        await vm.CleanUpHistoryCommand.ExecuteAsync(null);
        Assert.Equal(3, ProfileHistory.Read(profile).Versions.Count);
    }

    [Fact]
    public async Task PendingModifier_IsSavedByTheAutosave_AndComesBackWithTheProfile()
    {
        MainWindowViewModel.AutosaveDelay = TimeSpan.FromHours(1);
        HistoryFakeEngine fake = new();
        MainWindowViewModel vm = CreateVm(fake);
        string profile = NewProfile("drafts");
        File.WriteAllText(profile, "profile");
        vm.ProfilePath = profile;
        vm.ModifierCreateName = "Shift";
        vm.ModifierCreateDevice = "Keyboard";
        vm.ModifierCreateInput = "LShift";

        await vm.CreateModifierOnSelectedBindCommand.ExecuteAsync(null);
        Assert.True(vm.NeedsAutosaveFlush);
        Assert.Equal("Saving…", vm.SaveStateText);

        // Closing the window flushes.
        await vm.FlushAutosaveAsync();
        Assert.False(vm.NeedsAutosaveFlush);
        Assert.Contains("Shift", ProfileHistory.ReadDrafts(profile));

        MainWindowViewModel reopened = CreateVm(new HistoryFakeEngine());
        reopened.ProfilePath = profile;
        await reopened.LoadProfileCommand.ExecuteAsync(null);
        Assert.Contains(reopened.ModifierItems, m => m.Name == "Shift" && m.IsPending);
    }

    [Fact]
    public async Task SwitchingProfile_SavesThePendingEditsIntoTheOldOne()
    {
        MainWindowViewModel.AutosaveDelay = TimeSpan.FromHours(1);
        HistoryFakeEngine fake = new();
        MainWindowViewModel vm = CreateVm(fake);
        string first = NewProfile("switch-a");
        string second = NewProfile("switch-b");
        File.WriteAllText(first, "a");
        File.WriteAllText(second, "b");
        vm.ProfilePath = first;
        vm.ModifierCreateName = "Alt";
        vm.ModifierCreateDevice = "Keyboard";
        vm.ModifierCreateInput = "LAlt";
        await vm.CreateModifierOnSelectedBindCommand.ExecuteAsync(null);

        vm.LastProfilePath = second;
        await vm.OpenLastProfileCommand.ExecuteAsync(null);

        Assert.Equal(second, vm.ProfilePath);
        Assert.Contains("Alt", ProfileHistory.ReadDrafts(first));
        Assert.Equal(string.Empty, ProfileHistory.ReadDrafts(second));
        Assert.DoesNotContain(vm.ModifierItems, m => m.Name == "Alt");
    }

    [Fact]
    public void ExportAndImportCommands_FlushTheAutosaveFirst()
    {
        Assert.True(MainWindowViewModel.IsExportOrImportCommand(["dcs", "export", "--mode", "merge"]));
        Assert.True(MainWindowViewModel.IsExportOrImportCommand(["il2", "import"]));
        Assert.True(MainWindowViewModel.IsExportOrImportCommand(["warthunder", "export"]));
        Assert.False(MainWindowViewModel.IsExportOrImportCommand(["relations", "add", "Gear"]));
        Assert.False(MainWindowViewModel.IsExportOrImportCommand(["dcs", "scan"]));
    }

    [Fact]
    public async Task NewProfile_GetsAnAutomaticPath_AndCanBeRenamed_WithItsHistory()
    {
        HistoryFakeEngine fake = new() { WriteProfiles = true };
        MainWindowViewModel vm = CreateVm(fake);

        await vm.NewProfileAutoCommand.ExecuteAsync(null);
        Assert.True(vm.HasProfile);
        string auto = vm.ProfilePath;
        Assert.StartsWith(Environment.GetEnvironmentVariable("JOYPRO_PROFILES_DIR")!, auto, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(auto));
        Save(auto, "A", Items(("One", 1)), "First");

        string target = Path.Combine(Path.GetDirectoryName(auto)!, "My Hornet.pr0file");
        Assert.True(await vm.RenameProfileAsync(target));
        Assert.Equal(target, vm.ProfilePath);
        Assert.Equal(target, vm.LastProfilePath);
        Assert.False(File.Exists(auto));
        Assert.Single(ProfileHistory.Read(target).Versions);
        Assert.Equal("My Hornet", vm.ProfileDisplayName);
    }

    // Tester report (2.3.1): renaming onto an existing profile asked to replace it and then did nothing. Now the yes
    // replaces it: the old file is kept as "<name>.pr0file.bak" with its history, the renamed profile is the open one
    // and its history follows it.
    private async Task<(MainWindowViewModel Vm, string Renamed, string Existing)> TwoProfilesAsync(Func<ConfirmRequest, Task<bool>> answer)
    {
        HistoryFakeEngine fake = new() { WriteProfiles = true };
        MainWindowViewModel vm = CreateVm(fake);
        await vm.NewProfileAutoCommand.ExecuteAsync(null);
        string renamed = vm.ProfilePath;
        Save(renamed, "RENAMED", Items(("One", 1)), "First");
        Save(renamed, "RENAMED 2", Items(("One", 2)), "Second");
        // The profiles folder is shared by the run: a name of its own per test.
        string existing = Path.Combine(Path.GetDirectoryName(renamed)!, "Existing " + Guid.NewGuid().ToString("N")[..8] + ".pr0file");
        Save(existing, "EXISTING", Items(("Old", 3)), "Old one");
        vm.ConfirmHandler = answer;
        return (vm, renamed, existing);
    }

    [Fact]
    public async Task Rename_OntoAnExistingProfile_ReplacesItAfterYes_AndKeepsABackupWithItsHistory()
    {
        ConfirmRequest? asked = null;
        (MainWindowViewModel vm, string renamed, string existing) = await TwoProfilesAsync(r => { asked = r; return Task.FromResult(true); });

        Assert.True(await vm.RenameProfileAsync(existing));

        Assert.NotNull(asked);
        Assert.Contains("Existing", asked!.Title);
        Assert.Contains(Path.GetFileName(existing) + ".bak", asked.Message);
        Assert.Equal("RENAMED 2", File.ReadAllText(existing));
        Assert.False(File.Exists(renamed));
        Assert.Equal(existing, vm.ProfilePath);
        Assert.Equal(existing, vm.LastProfilePath);
        Assert.Equal(2, ProfileHistory.Read(existing).Versions.Count);
        string backup = existing + ".bak";
        Assert.Equal("EXISTING", File.ReadAllText(backup));
        Assert.Single(ProfileHistory.Read(backup).Versions);
        Assert.Contains(Path.GetFileName(existing) + ".bak", vm.StatusText);

        // A second replace never overwrites the first backup.
        Save(existing, "AGAIN", Items(("One", 3)), "Third");
        string third = Path.Combine(root, "Third.pr0file");
        File.WriteAllText(third, "THIRD");
        vm.ProfilePath = third;
        Assert.True(await vm.RenameProfileAsync(existing));
        Assert.Equal("EXISTING", File.ReadAllText(backup));
        Assert.Contains(Directory.GetFiles(Path.GetDirectoryName(existing)!, Path.GetFileName(existing) + ".*.bak"), f => File.ReadAllText(f) == "AGAIN");
    }

    [Fact]
    public async Task Rename_OntoAnExistingProfile_Cancelled_ChangesNothing()
    {
        (MainWindowViewModel vm, string renamed, string existing) = await TwoProfilesAsync(_ => Task.FromResult(false));

        Assert.False(await vm.RenameProfileAsync(existing));

        Assert.Equal(renamed, vm.ProfilePath);
        Assert.Equal("RENAMED 2", File.ReadAllText(renamed));
        Assert.Equal("EXISTING", File.ReadAllText(existing));
        Assert.False(File.Exists(existing + ".bak"));
        Assert.Equal(2, ProfileHistory.Read(renamed).Versions.Count);
        Assert.Single(ProfileHistory.Read(existing).Versions);
        Assert.Contains("Cancelled", vm.StatusText);
    }

    [Fact]
    public async Task Rename_OntoAProfileThatIsOpenElsewhere_SaysSo_AndChangesNothing()
    {
        (MainWindowViewModel vm, string renamed, string existing) = await TwoProfilesAsync(_ => Task.FromResult(true));

        using (new FileStream(existing, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.False(await vm.RenameProfileAsync(existing));

        Assert.Contains("open in another program or locked", vm.StatusText);
        Assert.Equal(renamed, vm.ProfilePath);
        Assert.Equal("RENAMED 2", File.ReadAllText(renamed));
        Assert.Equal("EXISTING", File.ReadAllText(existing));
        Assert.False(File.Exists(existing + ".bak"));

        // Read-only counts as locked too.
        File.SetAttributes(existing, FileAttributes.ReadOnly);
        try
        {
            Assert.False(await vm.RenameProfileAsync(existing));
            Assert.Contains("read-only", vm.StatusText);
            Assert.Equal("RENAMED 2", File.ReadAllText(renamed));
        }
        finally
        {
            File.SetAttributes(existing, FileAttributes.Normal);
        }
    }

    // The top bar (Saved chip, Undo / Redo, History) and the open History panel with Details, rendered offscreen when
    // JOYPRO_UI_DESIGN_OUT names a folder (like UiDesignRenderTests): history-panel-<theme>.png.
    [Fact]
    public void RenderHistoryPanel()
    {
        string? outDir = Environment.GetEnvironmentVariable("JOYPRO_UI_DESIGN_OUT");
        if (string.IsNullOrWhiteSpace(outDir))
            return;
        Directory.CreateDirectory(outDir);
        AvaloniaRenderThread.Run(() =>
        {
            MainWindowViewModel vm = new(new UiDesignRenderTests.DesignFakeEngine(), new AxisTuningFakeCapture(), loadPersistedUiSettings: false)
            {
                EnginePath = typeof(MainWindowViewModel).Assembly.Location
            };
            string profile = NewProfile("My Sticks");
            DateTime real = DateTime.UtcNow;
            now = real.AddDays(-2);
            Save(profile, "A", Items(("Pitch", 3), ("Roll", 3)), "Import from DCS");
            now = real.AddHours(-3);
            ProfileHistory.Checkpoint(profile, "Before export to DCS");
            now = real.AddMinutes(-20);
            Save(profile, "B", Items(("Pitch", 3), ("Roll", 3), ("Throttle ALL", 42)), "Adding relation");
            now = real.AddMinutes(-5);
            Save(profile, "C", Items(("Pitch", 5), ("Roll", 3), ("Throttle ALL", 42)), "Adding actions");
            ProfileHistory.SetKeep(profile, 1, true);
            vm.ProfilePath = profile;
            vm.IsHistoryPanelOpen = true;
            vm.ShowHistoryVersionChangesCommand.Execute(vm.HistoryVersions[1]);
            foreach ((string theme, Avalonia.Styling.ThemeVariant variant) in new[] { ("dark", Avalonia.Styling.ThemeVariant.Dark), ("light", Avalonia.Styling.ThemeVariant.Light) })
            {
                Avalonia.Application.Current!.RequestedThemeVariant = variant;
                AvaloniaRenderThread.Pump();
                UiDesignRenderTests.RenderWindow(vm, 1600, 900, Path.Combine(outDir, "history-panel-" + theme + ".png"));
            }
            Avalonia.Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Default;
        });
    }

    // ---- helpers -------------------------------------------------------------------------------------------------------

    private string NewProfile(string name) => Path.Combine(root, name + ".pr0file");

    private static List<string> Items(params (string Name, int Actions)[] relations) =>
        relations.Select(r => "R\t" + r.Name + "\t" + r.Actions + "\t\th" + r.Name.GetHashCode() + "-" + r.Actions + "\tm").ToList();

    // A save as the engine does it: the file, then the version.
    private static ProfileVersion Save(string profile, string content, List<string> items, string label, ProfileHistoryPolicy? policy = null) =>
        SaveBytes(profile, Encoding.UTF8.GetBytes(content), items, label, policy);

    private static ProfileVersion SaveBytes(string profile, byte[] content, List<string> items, string label, ProfileHistoryPolicy? policy = null)
    {
        ProfileHistory.WriteFileAtomic(profile, content);
        return ProfileHistory.Record(profile, content, new ProfileHistoryRecordOptions { Label = label, Items = () => items }, policy)!;
    }

    // Recording without the clean-up a save runs every 6 hours (the simulated clock jumps months).
    private static readonly ProfileHistoryPolicy NoAutoPrune = new() { AutoPruneHours = double.MaxValue };

    private static MainWindowViewModel CreateVm(IEngineCliClient fake) =>
        new(fake, new AxisTuningFakeCapture(), loadPersistedUiSettings: false)
        {
            EnginePath = typeof(MainWindowViewModel).Assembly.Location
        };

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++)
            await Task.Delay(10);
        Assert.True(condition());
    }

    // Answers every command with exit 0; "profile new" writes the file like the engine.
    private sealed class HistoryFakeEngine : IEngineCliClient
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];

        public bool WriteProfiles { get; init; }

        public Task<EngineCommandResult> RunAsync(string engineExePath, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            lock (Calls)
                Calls.Add(arguments.ToArray());
            int at = arguments.ToList().IndexOf("new");
            if (WriteProfiles && at > 0 && arguments[at - 1] == "profile" && at + 1 < arguments.Count)
                File.WriteAllText(arguments[at + 1], "empty profile");
            return Task.FromResult(new EngineCommandResult
            {
                ExitCode = 0,
                Duration = TimeSpan.FromMilliseconds(1),
                StandardOutput = "ok",
                StandardError = string.Empty,
                Arguments = arguments.ToArray()
            });
        }
    }
}
