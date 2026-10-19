using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.History;

namespace JoyPro.Gui.ViewModels;

// Autosave, the profile history and the last profile at start (docs/profile-history.md):
//   - Every engine command that changes the profile saves it (atomically) and the engine records a version. What the GUI
//     holds before it reaches the profile (axis tuning edits, marker positions and callout tables of the Layout page,
//     modifiers that are not on a bind yet) is saved 2 s after the last change, and at once when JoyPro closes, another
//     profile opens, and before an export or import. The top bar shows "Saving..." / "Saved".
//   - History panel: the versions with what changed, details, compare with now, restore (a version itself), keep, the
//     space used and Clean up. Undo / Redo (Ctrl+Z / Ctrl+Y) walk the history, also across sessions.
//   - The last profile opens after the window shows (Settings: "Open the last profile at start").
public sealed partial class MainWindowViewModel
{
    // ---- Autosave ---------------------------------------------------------------------------------------------------

    // Tests shorten it.
    public static TimeSpan AutosaveDelay { get; set; } = TimeSpan.FromSeconds(2);

    private AutosaveScheduler? autosave;
    private int profileWritesRunning;
    private DateTime? lastProfileSaveLocal;
    private string lastProfileSaveError = string.Empty;
    private string lastDraftsJson = string.Empty;
    private bool profileHistoryInitialized;
    private bool historyMaintenanceStarted;
    private ProfileHistoryLog? historyLog;

    internal AutosaveScheduler Autosave => autosave ??= CreateAutosave();

    private AutosaveScheduler CreateAutosave()
    {
        AutosaveScheduler scheduler = new(SaveProfileDraftsAsync, AutosaveDelay);
        scheduler.StateChanged += (_, _) =>
        {
            lastProfileSaveError = scheduler.LastError?.Message ?? string.Empty;
            if (scheduler.LastError == null && scheduler.LastSavedUtc.HasValue && !scheduler.IsPending)
                lastProfileSaveLocal = scheduler.LastSavedUtc.Value.ToLocalTime();
            RaiseSaveState();
        };
        return scheduler;
    }

    // Called once from the constructor: the edits that are saved by the autosave.
    private void InitializeProfileHistory()
    {
        if (profileHistoryInitialized)
            return;
        profileHistoryInitialized = true;
        PropertyChanged += OnAutosaveSourceChanged;
        ModifierItems.CollectionChanged += (_, _) =>
        {
            if (HasProfile && (pendingModifiersByName.Count > 0 || lastDraftsJson.Length > 0) && BuildProfileDraftsJson() != lastDraftsJson)
                ScheduleAutosave();
        };
        HistoryVersions.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasHistoryVersions));
        HistoryCompareLines.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasHistoryCompareLines));
    }

    private void OnAutosaveSourceChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            // Axis tuning: every edit recomputes the curve; saved for the shown scope.
            case nameof(AxisTuningCurve):
            case nameof(AxisTuningHasEdits):
                if (AxisTuningHasEdits)
                    ScheduleAutosave();
                break;
            // Layout page: marker positions and callout tables of the device pictures (every edit changes the undo tip).
            case nameof(LayoutUndoTip):
            case nameof(WorkspaceHasUnsavedChanges):
                if (WorkspaceHasUnsavedChanges && !LayoutShowsLayoutFile)
                    ScheduleAutosave();
                break;
        }
    }

    // The Layout page's device pictures are saved without a profile too; the rest needs one.
    public void ScheduleAutosave() => Autosave.Schedule();

    // Saves what is pending now (closing, switching profile, before an export or import).
    public Task FlushAutosaveAsync() => autosave == null ? Task.CompletedTask : autosave.FlushAsync();

    // "dcs export", "il2 import", "warthunder export", "relations import": the profile is read or replaced as a whole.
    internal static bool IsExportOrImportCommand(IReadOnlyList<string> command) =>
        command.Count >= 2 && (string.Equals(command[1], "export", StringComparison.OrdinalIgnoreCase) || string.Equals(command[1], "import", StringComparison.OrdinalIgnoreCase));

    // The window asks before it closes.
    public bool NeedsAutosaveFlush => autosave != null && (autosave.IsPending || autosave.IsSaving);

    // The edits the profile does not have yet.
    private async Task SaveProfileDraftsAsync(CancellationToken cancellationToken)
    {
        if (WorkspaceHasUnsavedChanges && !LayoutShowsLayoutFile && CanSaveWorkspace)
            AutosaveReferenceWorkspace();
        if (!HasProfile)
            return;
        if (AxisTuningHasEdits && CanSaveAxisTuning)
            await SaveAxisTuningAsync();
        WriteProfileDrafts();
    }

    // ---- Save state (top bar) ---------------------------------------------------------------------------------------

    public bool IsProfileSaving => HasProfile && (profileWritesRunning > 0 || (autosave != null && (autosave.IsPending || autosave.IsSaving)));

    public bool HasProfileSaveError => HasProfile && !IsProfileSaving && lastProfileSaveError.Length > 0;

    public bool IsProfileSaved => HasProfile && !IsProfileSaving && lastProfileSaveError.Length == 0;

    public string SaveStateText => !HasProfile ? string.Empty
        : IsProfileSaving ? T("Saving…")
        : lastProfileSaveError.Length > 0 ? T("Not saved")
        : T("Saved ✓");

    public string SaveStateTip => !HasProfile ? string.Empty
        : IsProfileSaving ? T("Saving your changes.")
        : lastProfileSaveError.Length > 0 ? T("Saving failed: {0} JoyPro tries again with your next change.", lastProfileSaveError)
        : T("Every change is saved automatically{0}. History lists the versions; Undo goes back.", (lastProfileSaveLocal.HasValue ? T(" (last at {0})", lastProfileSaveLocal.Value.ToString("HH:mm:ss", CultureInfo.InvariantCulture)) : string.Empty));

    private void RaiseSaveState()
    {
        OnPropertyChanged(nameof(IsProfileSaving));
        OnPropertyChanged(nameof(IsProfileSaved));
        OnPropertyChanged(nameof(HasProfileSaveError));
        OnPropertyChanged(nameof(SaveStateText));
        OnPropertyChanged(nameof(SaveStateTip));
    }

    // RunEngineAsync around every command that changes the profile.
    private void BeginProfileWrite()
    {
        profileWritesRunning++;
        RaiseSaveState();
    }

    private void EndProfileWrite(bool ok, string error)
    {
        profileWritesRunning = Math.Max(0, profileWritesRunning - 1);
        if (ok)
        {
            lastProfileSaveLocal = DateTime.Now;
            lastProfileSaveError = string.Empty;
        }
        else if (error.Length > 0)
        {
            lastProfileSaveError = error;
        }
        RaiseSaveState();
        RefreshHistoryState();
    }

    // The profile changes: its pending edits stay with it (flushed before, or forgotten when the switch was not asked
    // for through FlushAutosaveAsync), and its history is read.
    partial void OnProfilePathChanging(string value)
    {
        autosave?.Cancel();
        lastDraftsJson = string.Empty;
    }

    private void OnHistoryProfileChanged()
    {
        lastProfileSaveError = string.Empty;
        lastProfileSaveLocal = HasProfile && File.Exists(ProfilePath.Trim()) ? File.GetLastWriteTime(ProfilePath.Trim()) : null;
        HistoryCompareTitle = string.Empty;
        HistoryCompareLines.Clear();
        RaiseSaveState();
        RefreshHistoryState();
    }

    // ---- Drafts: modifiers that are not on a bind yet -------------------------------------------------------------------

    private sealed class ProfileDraftsDocument
    {
        public List<PendingModifierDraft> PendingModifiers { get; set; } = [];
    }

    private sealed class PendingModifierDraft
    {
        public string Name { get; set; } = string.Empty;
        public string Device { get; set; } = string.Empty;
        public string Input { get; set; } = string.Empty;
        public bool Inverted { get; set; }
    }

    private static readonly JsonSerializerOptions DraftsJsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    private string BuildProfileDraftsJson()
    {
        HashSet<string> inProfile = new(ModifierItems.Where(m => !m.IsPending).Select(m => m.Name), StringComparer.OrdinalIgnoreCase);
        List<PendingModifierDraft> pending = pendingModifiersByName.Values
            .Where(p => !inProfile.Contains(p.Name))
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(p => new PendingModifierDraft { Name = p.Name, Device = p.Device, Input = p.Input, Inverted = p.Inverted })
            .ToList();
        return pending.Count == 0 ? string.Empty : JsonSerializer.Serialize(new ProfileDraftsDocument { PendingModifiers = pending }, DraftsJsonOptions);
    }

    private void WriteProfileDrafts()
    {
        if (!HasProfile || !ProfileHistory.Enabled)
            return;
        string json = BuildProfileDraftsJson();
        if (json == lastDraftsJson)
            return;
        ProfileHistory.WriteDrafts(ProfilePath.Trim(), json);
        lastDraftsJson = json;
    }

    // After a profile is opened: its modifiers that were not on a bind yet come back.
    private void RestoreProfileDrafts()
    {
        if (!HasProfile)
            return;
        string json = ProfileHistory.ReadDrafts(ProfilePath.Trim());
        lastDraftsJson = json;
        if (json.Length == 0)
            return;
        try
        {
            ProfileDraftsDocument? drafts = JsonSerializer.Deserialize<ProfileDraftsDocument>(json, DraftsJsonOptions);
            foreach (PendingModifierDraft draft in drafts?.PendingModifiers ?? [])
            {
                if (string.IsNullOrWhiteSpace(draft.Name) || pendingModifiersByName.ContainsKey(draft.Name))
                    continue;
                pendingModifiersByName[draft.Name.Trim()] = new PendingModifierDefinition(draft.Name.Trim(), draft.Device ?? string.Empty, draft.Input ?? string.Empty, draft.Inverted);
            }
        }
        catch (JsonException)
        {
            // A damaged drafts file only loses the drafts.
        }
    }

    // ---- Undo / redo -------------------------------------------------------------------------------------------------

    public bool CanUndo => historyLog?.CanUndo == true;

    public string UndoLabel => historyLog?.UndoSubject is { } subject ? "Undo: " + subject.Description : "Nothing to undo";

    public bool CanRedo => historyLog?.CanRedo == true;

    public string RedoLabel => historyLog?.RedoTarget is { } target ? "Redo: " + target.Description : "Nothing to redo";

    // Reads the open profile's history (cheap: one small JSON file) and updates Undo, Redo and the panel.
    internal void RefreshHistoryState()
    {
        historyLog = null;
        if (HasProfile && ProfileHistory.Enabled)
        {
            try
            {
                historyLog = ProfileHistory.Read(ProfilePath.Trim());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                historyLog = null;
            }
        }
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(UndoLabel));
        OnPropertyChanged(nameof(CanRedo));
        OnPropertyChanged(nameof(RedoLabel));
        if (IsHistoryPanelOpen)
            RebuildHistoryVersions();
    }

    [RelayCommand]
    private async Task UndoLastChangeAsync() => await WriteHistoryAsync(ProfileHistory.Undo, "Undo");

    [RelayCommand]
    private async Task RedoLastChangeAsync() => await WriteHistoryAsync(ProfileHistory.Redo, "Redo");

    private async Task WriteHistoryAsync(Func<string, ProfileHistoryWriteResult> write, string what)
    {
        if (!HasProfile)
        {
            StatusText = T("Open a profile first.");
            return;
        }
        // Edits that are not saved yet are a change of their own: saved first, so the undo takes them back first.
        await FlushAutosaveAsync();
        string profile = ProfilePath.Trim();
        ProfileHistoryWriteResult result;
        // No engine command runs meanwhile: it would save the profile it loaded before the undo over it.
        await engineCallGate.WaitAsync();
        try
        {
            BeginProfileWrite();
            result = await Task.Run(() => write(profile));
        }
        catch (Exception ex)
        {
            EndProfileWrite(false, ex.Message);
            StatusText = T("{0} failed: {1}", what, ex.Message);
            return;
        }
        finally
        {
            engineCallGate.Release();
        }
        EndProfileWrite(true, string.Empty);
        StatusText = Translate(result.Message);
        if (result.Changed)
            await ReloadAfterHistoryWriteAsync();
        RefreshHistoryState();
    }

    // The profile file was replaced by a version: the pages show it again.
    private async Task ReloadAfterHistoryWriteAsync()
    {
        string status = StatusText;
        string selected = SelectedRelation?.Name ?? string.Empty;
        await RefreshRelationsAsync();
        await RefreshBindsAsync();
        await RefreshModifiersAsync();
        if (selected.Length > 0)
            SelectRelationByName(selected);
        StatusText = status;
    }

    // ---- History panel -----------------------------------------------------------------------------------------------

    private const int HistoryListLimit = 400;

    [ObservableProperty]
    private bool isHistoryPanelOpen;

    partial void OnIsHistoryPanelOpenChanged(bool value)
    {
        if (value)
        {
            RefreshHistoryState();
            RefreshHistoryUsage();
        }
        else
        {
            HistoryCompareTitle = string.Empty;
            HistoryCompareLines.Clear();
        }
    }

    public ObservableCollection<HistoryVersionItem> HistoryVersions { get; } = [];

    public bool HasHistoryVersions => HistoryVersions.Count > 0;

    [ObservableProperty]
    private string historyUsageText = string.Empty;

    [ObservableProperty]
    private string historyCompareTitle = string.Empty;

    partial void OnHistoryCompareTitleChanged(string value) => OnPropertyChanged(nameof(HasHistoryCompare));

    public bool HasHistoryCompare => HistoryCompareTitle.Length > 0;

    public ObservableCollection<string> HistoryCompareLines { get; } = [];

    public bool HasHistoryCompareLines => HistoryCompareLines.Count > 0;

    public string HistoryEmptyText => !HasProfile
        ? T("Open a profile to see its history.")
        : !ProfileHistory.Enabled
            ? T("The profile history is switched off (JOYPRO_PROFILE_HISTORY=off).")
            : T("No versions yet: the first change you make is the first version.");

    [RelayCommand]
    private void ToggleHistoryPanel() => IsHistoryPanelOpen = !IsHistoryPanelOpen;

    private void RebuildHistoryVersions()
    {
        ProfileHistoryLog? log = historyLog;
        int cursor = log?.CursorVersion?.Id ?? 0;
        DateTime now = DateTime.Now;
        List<HistoryVersionItem> items = log == null
            ? []
            : log.Versions.AsEnumerable().Reverse().Take(HistoryListLimit).Select(v => new HistoryVersionItem(v, v.Id == cursor, now)).ToList();
        HistoryVersions.Clear();
        foreach (HistoryVersionItem item in items)
            HistoryVersions.Add(item);
        OnPropertyChanged(nameof(HistoryEmptyText));
    }

    private void RefreshHistoryUsage()
    {
        if (!ProfileHistory.Enabled)
        {
            HistoryUsageText = string.Empty;
            return;
        }
        try
        {
            ProfileHistoryPolicy policy = ProfileHistoryPolicy.Default;
            ProfileHistoryUsage all = ProfileHistory.UsageAll();
            string text = T("All profiles: {0} of {1}", ProfileHistory.FormatBytes(all.Bytes), ProfileHistory.FormatBytes(policy.GlobalCapBytes));
            if (HasProfile)
            {
                ProfileHistoryUsage mine = ProfileHistory.Usage(ProfilePath.Trim());
                text = T("This profile: {0} versions{1}, {2} of {3} · {4}", mine.Versions.ToString(CultureInfo.InvariantCulture), (mine.Kept > 0 ? T(" ({0} kept)", mine.Kept.ToString(CultureInfo.InvariantCulture)) : string.Empty), ProfileHistory.FormatBytes(mine.Bytes), ProfileHistory.FormatBytes(policy.ProfileCapBytes), text);
            }
            HistoryUsageText = text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            HistoryUsageText = string.Empty;
        }
    }

    // Writes the version back (a new version: Undo returns to the state before).
    [RelayCommand]
    private async Task RestoreHistoryVersionAsync(HistoryVersionItem? item)
    {
        if (item == null || !HasProfile || item.IsCurrent)
            return;
        if (!await ConfirmAsync(T("Go back to {0}?", item.WhenText),
                T("The profile becomes as it was then ({0}). The current state stays in the history: Undo (Ctrl+Z) brings it back.", item.Title), T("Restore")))
            return;
        int id = item.Id;
        await WriteHistoryAsync(profile => ProfileHistory.Restore(profile, id), "Restore");
    }

    // What this version changed against the one it was made on.
    [RelayCommand]
    private void ShowHistoryVersionChanges(HistoryVersionItem? item)
    {
        if (item == null || historyLog == null)
            return;
        ProfileVersion? version = historyLog.Find(item.Id);
        if (version == null)
            return;
        ShowHistoryDiff(T("What changed {0}: {1}", item.WhenText, item.Title), version.Parent, version.Id, version.Parent <= 0 ? T("The first version of the history: {0}", version.Summary) : null);
    }

    // The changes from this version to the profile as it is now.
    [RelayCommand]
    private void CompareHistoryVersion(HistoryVersionItem? item)
    {
        if (item == null || historyLog?.CursorVersion is not { } current)
            return;
        ShowHistoryDiff(T("From {0} to now", item.WhenText), item.Id, current.Id, item.Id == current.Id ? T("This is the version the profile is at.") : null);
    }

    private void ShowHistoryDiff(string title, int fromId, int toId, string? fallback)
    {
        HistoryCompareLines.Clear();
        HistoryCompareTitle = title;
        List<string>? changes = fromId > 0 && toId > 0 ? ProfileHistory.Diff(ProfilePath.Trim(), fromId, toId) : null;
        if (changes == null)
        {
            HistoryCompareLines.Add(fallback ?? T("No details: one of the two versions was changed outside JoyPro."));
            return;
        }
        if (changes.Count == 0)
            HistoryCompareLines.Add(fallback ?? T("No change in relations, binds or settings."));
        foreach (string change in changes.Take(500))
            HistoryCompareLines.Add(change);
        if (changes.Count > 500)
            HistoryCompareLines.Add(T("... and {0} more changes.", (changes.Count - 500).ToString(CultureInfo.InvariantCulture)));
    }

    [RelayCommand]
    private void CloseHistoryCompare()
    {
        HistoryCompareTitle = string.Empty;
        HistoryCompareLines.Clear();
    }

    // A kept version is never cleaned up.
    [RelayCommand]
    private void ToggleHistoryKeep(HistoryVersionItem? item)
    {
        if (item == null || !HasProfile)
            return;
        bool keep = !item.Keep;
        bool done;
        try
        {
            done = ProfileHistory.SetKeep(ProfilePath.Trim(), item.Id, keep);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = T("Could not keep the version: {0}", ex.Message);
            return;
        }
        if (done)
        {
            item.Keep = keep;
            StatusText = keep ? T("Kept: the version of {0} is never cleaned up.", item.WhenText) : T("The version of {0} may be cleaned up again.", item.WhenText);
            historyLog = ProfileHistory.Read(ProfilePath.Trim());
            RefreshHistoryUsage();
        }
    }

    // The retention right now (it also runs in the background a minute after start and every 6 hours).
    [RelayCommand]
    private async Task CleanUpHistoryAsync()
    {
        if (!ProfileHistory.Enabled)
            return;
        StatusText = T("Cleaning up the profile history...");
        try
        {
            ProfileHistoryPruneResult result = await Task.Run(() => RunAtLowPriority(() => ProfileHistory.PruneAll()));
            StatusText = result.Removed.Count == 0
                ? T("The history is tidy: nothing to clean up ({0}).", ProfileHistory.FormatBytes(result.BytesAfter))
                : T("Cleaned up {0} old versions: {1} -> {2}.", result.Removed.Count.ToString(CultureInfo.InvariantCulture), ProfileHistory.FormatBytes(result.BytesBefore), ProfileHistory.FormatBytes(result.BytesAfter));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = T("Clean-up failed: {0}", ex.Message);
        }
        RefreshHistoryState();
        RefreshHistoryUsage();
    }

    // Background clean-up at low priority: a minute after start, then every 6 hours.
    public void StartHistoryMaintenance()
    {
        if (!persistUiFiles || historyMaintenanceStarted || !ProfileHistory.Enabled)
            return;
        historyMaintenanceStarted = true;
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMinutes(1));
            while (true)
            {
                try
                {
                    RunAtLowPriority(() => ProfileHistory.PruneAll());
                }
                catch (Exception ex)
                {
                    JoyPro.Logging.JoyProLog.Warn("History", "Background clean-up of the profile history failed", ex);
                }
                await Task.Delay(TimeSpan.FromHours(6));
            }
        });
    }

    private static T RunAtLowPriority<T>(Func<T> work)
    {
        Thread thread = Thread.CurrentThread;
        ThreadPriority before = thread.Priority;
        try
        {
            thread.Priority = ThreadPriority.Lowest;
            return work();
        }
        finally
        {
            thread.Priority = before;
        }
    }

    // ---- New / rename / save as ---------------------------------------------------------------------------------------

    // "New profile": an empty profile with a name of its own in the profiles folder (Rename... gives it yours).
    [RelayCommand]
    private async Task NewProfileAutoAsync()
    {
        await FlushAutosaveAsync();
        ProfilePath = CreateFreshAutoProfilePath();
        await NewProfileAsync();
        if (HasProfile && File.Exists(ProfilePath))
            StatusText = T("New profile {0} in {1}. Rename... gives it your name.", ProfileDisplayName, Path.GetDirectoryName(ProfilePath));
    }

    // Renames the profile file; its history goes along.
    public async Task<bool> RenameProfileAsync(string newPath)
    {
        if (!HasProfile || string.IsNullOrWhiteSpace(newPath))
            return false;
        string old = Path.GetFullPath(ProfilePath.Trim());
        string target = Path.GetFullPath(newPath.Trim());
        if (!target.EndsWith(".pr0file", StringComparison.OrdinalIgnoreCase))
            target += ".pr0file";
        if (string.Equals(old, target, StringComparison.OrdinalIgnoreCase))
            return false;
        if (File.Exists(target))
        {
            StatusText = T("There is a profile with that name already: {0}", target);
            return false;
        }
        await FlushAutosaveAsync();
        try
        {
            EnsureProfileDirectoryExists(target);
            File.Move(old, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = T("Renaming failed: {0}", ex.Message);
            return false;
        }
        ProfileHistory.Move(old, target);
        ProfilePath = target;
        RememberLastProfilePath(target);
        RestoreProfileDrafts();
        RefreshHistoryState();
        StatusText = T("Renamed to {0}.", ProfileDisplayName);
        return true;
    }

    // A copy under another name; JoyPro works on the copy from now on.
    public async Task<bool> SaveProfileAsAsync(string newPath)
    {
        if (!HasProfile || string.IsNullOrWhiteSpace(newPath))
            return false;
        string target = Path.GetFullPath(newPath.Trim());
        if (!target.EndsWith(".pr0file", StringComparison.OrdinalIgnoreCase))
            target += ".pr0file";
        if (string.Equals(Path.GetFullPath(ProfilePath.Trim()), target, StringComparison.OrdinalIgnoreCase))
        {
            await SaveProfileAsync();
            return true;
        }
        await FlushAutosaveAsync();
        string source = ProfileDisplayName;
        string drafts = BuildProfileDraftsJson();
        EnsureProfileDirectoryExists(target);
        EngineCommandResult? result = await RunEngineAsync(["profile", "save", target], includeProfile: true, mutatesProfile: false,
            busyLabel: N("Saving a copy..."), historyNote: "Saved as a copy of " + source);
        if (result?.ExitCode != 0)
            return false;
        ProfilePath = target;
        RememberLastProfilePath(target);
        if (drafts.Length > 0 && ProfileHistory.Enabled)
            ProfileHistory.WriteDrafts(target, drafts);
        RestoreProfileDrafts();
        RefreshHistoryState();
        StatusText = T("Saved as {0}; JoyPro works on this copy now.", ProfileDisplayName);
        return true;
    }

    // ---- The last profile at start ------------------------------------------------------------------------------------

    // After the window shows: the last profile opens (busy area shows it), or the Start page stays as it is when the
    // file is gone. Settings: "Open the last profile at start".
    public async Task AutoOpenLastProfileOnStartupAsync()
    {
        if (!LoadLastProfileOnStartup || HasProfile)
            return;
        string candidate = (LastProfilePath ?? string.Empty).Trim();
        if (candidate.Length == 0)
            return;
        if (!File.Exists(candidate))
        {
            StatusText = T("Your last profile is no longer there ({0}). Open one, start a new one, or import from a game.", candidate);
            return;
        }
        StatusText = T("Opening your last profile {0}...", Path.GetFileNameWithoutExtension(candidate));
        openingAtStartup = true;
        try
        {
            await OpenLastProfileInternalAsync(showMissingStatus: true);
        }
        finally
        {
            openingAtStartup = false;
        }
    }

    // The last profile is opening at start: the Start page stays in front.
    private bool openingAtStartup;
}
