using System;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using JoyPro.History;

namespace JoyPro.Gui.Models;

// One version of the open profile in the History panel (docs/profile-history.md).
public sealed partial class HistoryVersionItem : ObservableObject
{
    public HistoryVersionItem(ProfileVersion version, bool isCurrent, DateTime nowLocal)
    {
        Id = version.Id;
        Kind = version.Kind ?? ProfileVersionKind.Change;
        Title = version.Title;
        Summary = (version.Summary ?? string.Empty).Trim();
        IsCurrent = isCurrent;
        keep = version.Keep;
        DateTime local = DateTime.SpecifyKind(version.Utc, DateTimeKind.Utc).ToLocalTime();
        WhenText = local.Date == nowLocal.Date
            ? T("Today {0}", local.ToString("HH:mm:ss", CultureInfo.InvariantCulture))
            : local.Date == nowLocal.Date.AddDays(-1)
                ? T("Yesterday {0}", local.ToString("HH:mm", CultureInfo.InvariantCulture))
                : local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        WhenTip = T("{0} · version {1}", local.ToString("dddd, yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), Id.ToString(CultureInfo.InvariantCulture));
    }

    public int Id { get; }

    public string Kind { get; }

    public string Title { get; }

    public string Summary { get; }

    public bool HasSummary => Summary.Length > 0 && !string.Equals(Summary, Title, StringComparison.Ordinal);

    public string WhenText { get; }

    public string WhenTip { get; }

    // The profile is at this version now.
    public bool IsCurrent { get; }

    public bool CanRestore => !IsCurrent;

    public bool IsCheckpoint => Kind == ProfileVersionKind.Checkpoint;

    public bool IsUndoOrRedo => Kind is ProfileVersionKind.Undo or ProfileVersionKind.Redo or ProfileVersionKind.Restore;

    // Never cleaned up.
    [ObservableProperty]
    private bool keep;

    partial void OnKeepChanged(bool value) => OnPropertyChanged(nameof(KeepTip));

    public string KeepTip =>Keep ? T("Kept: never cleaned up (click to let the clean-up remove it again)") : T("Keep this version forever");
}
