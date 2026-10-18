using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JoyPro.DeviceReference;
using JoyPro.Gui.Services;

namespace JoyPro.Gui.ViewModels;

// One game of the picture pack release on the Cockpits page: "DCS World: 48 cockpits, 900 MB" with its download button.
public sealed class CockpitPackGameItem
{
    public string Game { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public int Count { get; init; }

    public double SizeMb { get; init; }

    public string Summary => Plural("{0} cockpit, {1} MB", "{0} cockpits, {1} MB", Count, Count, SizeMb.ToString("0", Localization.Loc.Culture));
}

// The picture packs of JoyPro's releases on the Cockpits page (docs/cockpits.md "Picture packs", CockpitPicturePacks):
// an aircraft without pictures from the user's games gets "Download pictures for ...?" when a pack has it (Settings >
// Privacy: ask, always, never), and the packs of all the user's aircraft or of one game can be downloaded at once.
// The manifest is read when it is needed and kept for the session; downloads show their bytes and can be cancelled.
public sealed partial class CockpitsViewModel
{
    private Task<CockpitPicturePackManifest?>? manifestTask;
    private DateTime manifestFailedAt = DateTime.MinValue;
    private CancellationTokenSource? packDownload;
    private readonly HashSet<string> dismissedPacks = new(StringComparer.OrdinalIgnoreCase);
    private List<CockpitPicturePack> pendingPacks = [];

    // Where the packs come from (tests: a local folder); default JoyPro's release (JOYPRO_COCKPIT_PICTURE_FEED).
    // Another feed reads its list again.
    public string PictureFeed
    {
        get => pictureFeed;
        set
        {
            if (string.Equals(pictureFeed, value, StringComparison.OrdinalIgnoreCase))
                return;
            pictureFeed = value;
            manifestTask = null;
            partialManifest = null;
            manifestFailedAt = DateTime.MinValue;
            dismissedPacks.Clear();
        }
    }

    private string pictureFeed = CockpitPicturePacks.DefaultFeed;

    // How the feed is read and the packs downloaded (tests: a local transport); default https / file.
    public IUpdateTransport? PackTransport { get; set; }

    // ask, always or never (Settings > Privacy); tests replace it.
    public Func<string> PackMode { get; set; } = () => CockpitPicturePacks.DownloadMode;

    // Whether a game is installed (set by the main window); "all my aircraft" are the variants of installed games.
    public Func<string, bool>? IsGameInstalled { get; set; }

    public ObservableCollection<CockpitPackGameItem> PackGames { get; } = [];

    [ObservableProperty] private string packOfferText = string.Empty;
    [ObservableProperty] private bool hasPackOffer;
    [ObservableProperty] private bool isDownloadingPacks;
    [ObservableProperty] private double packProgress;
    [ObservableProperty] private string packProgressText = string.Empty;
    [ObservableProperty] private string packStatus = string.Empty;
    [ObservableProperty] private string packListStatus = string.Empty;
    [ObservableProperty] private bool packsExpanded;

    public bool HasPackGames => PackGames.Count > 0;

    // The check of the variant shown last (offer or download); tests wait for it.
    public Task PackCheck { get; private set; } = Task.CompletedTask;

    private CockpitPicturePacks Packs() => new(PictureFeed, PicturesRoot, PackTransport);

    // The manifest of the feeds, read once per session (after a failure again two minutes later). Null: none. A list with
    // a part that could not be read (a further feed, FailedFeeds) is used until it is read again two minutes later.
    public async Task<CockpitPicturePackManifest?> GetPackManifestAsync(bool quiet)
    {
        if (manifestTask == null && (DateTime.UtcNow - manifestFailedAt).TotalMinutes >= 2)
            manifestTask = LoadManifestAsync(quiet);
        if (manifestTask == null)
            return partialManifest;
        CockpitPicturePackManifest? manifest = await manifestTask;
        if (manifest == null || manifest.FailedFeeds.Count > 0)
        {
            manifestTask = null;
            manifestFailedAt = DateTime.UtcNow;
        }
        if (manifest == null)
            return partialManifest;
        partialManifest = manifest.FailedFeeds.Count > 0 ? manifest : null;
        return manifest;
    }

    private CockpitPicturePackManifest? partialManifest;

    private async Task<CockpitPicturePackManifest?> LoadManifestAsync(bool quiet)
    {
        try
        {
            return await Packs().LoadManifestAsync();
        }
        catch (Exception ex) when (ex is CockpitPicturePackException or OperationCanceledException or System.Net.Http.HttpRequestException)
        {
            if (!quiet)
                PackListStatus = ex.Message;
            return null;
        }
    }

    // After the variant changed: offer its pack (or download it) when it has no pictures from the user's games.
    public async Task CheckPicturePackAsync()
    {
        CockpitVariant? v = Variant;
        if (!HasPackOffer || pendingPacks.Count <= 1)
            ClearPackOffer();
        if (v == null || HasGamePictures || IsDownloadingPacks || HasPackOffer)
            return;
        string mode = PackMode();
        // Only variants of games packs exist for at all; "never" does not even read the list.
        if (mode == CockpitPicturePacks.Never || !v.Games.Any(g => CockpitPictureSet.Games.Contains(g.Game, StringComparer.OrdinalIgnoreCase)))
            return;
        CockpitPicturePackManifest? manifest = await GetPackManifestAsync(quiet: true);
        if (manifest == null || v != Variant || HasGamePictures || IsDownloadingPacks)
            return;
        CockpitPicturePack? pack = manifest.For(v);
        if (pack != null && !CockpitPicturePacks.Wanted(Packs().StateOf(pack), false))
            pack = null;
        switch (CockpitPicturePacks.OfferFor(mode, pack != null, HasGamePictures, pack != null && dismissedPacks.Contains(pack.Id)))
        {
            case CockpitPicturePacks.Ask:
                Offer([pack!], T("Download pictures for {0} ({1} MB)?", Title, pack!.SizeMb.ToString("0.0", Localization.Loc.Culture)));
                break;
            case CockpitPicturePacks.Always:
                await DownloadPacksAsync([pack!]);
                break;
        }
    }

    private void Offer(List<CockpitPicturePack> packs, string text)
    {
        pendingPacks = packs;
        PackOfferText = text;
        HasPackOffer = packs.Count > 0;
    }

    private void ClearPackOffer()
    {
        pendingPacks = [];
        PackOfferText = string.Empty;
        HasPackOffer = false;
    }

    [RelayCommand]
    private Task DownloadOfferedPacksAsync()
    {
        List<CockpitPicturePack> packs = pendingPacks;
        ClearPackOffer();
        return DownloadPacksAsync(packs);
    }

    // "Not now": not asked again for these packs in this session.
    [RelayCommand]
    private void DismissPackOffer()
    {
        foreach (CockpitPicturePack p in pendingPacks)
            dismissedPacks.Add(p.Id);
        ClearPackOffer();
    }

    [RelayCommand]
    private void CancelPackDownload() => packDownload?.Cancel();

    partial void OnPacksExpandedChanged(bool value)
    {
        if (value)
            PackGamesLoad = LoadPackGamesAsync();
    }

    // Reading the list for the expander (tests wait for it).
    public Task PackGamesLoad { get; private set; } = Task.CompletedTask;

    // The games of the pack release (the expander's list).
    public async Task LoadPackGamesAsync()
    {
        PackListStatus = T("Reading the list of picture packs...");
        CockpitPicturePackManifest? manifest = await GetPackManifestAsync(quiet: false);
        if (manifest == null)
        {
            if (PackListStatus == T("Reading the list of picture packs..."))
                PackListStatus = T("The list of picture packs could not be read.");
            return;
        }
        List<CockpitPackGameItem> games = manifest.Packs.GroupBy(p => p.Game, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => Array.IndexOf(CockpitPictureSet.Games, g.Key) is int i && i >= 0 ? i : 99)
            .Select(g => new CockpitPackGameItem { Game = g.Key, Name = CockpitManualPages.GameName(g.Key), Count = g.Count(), SizeMb = g.Sum(p => p.SizeMb) })
            .ToList();
        PackGames.Clear();
        foreach (CockpitPackGameItem game in games)
            PackGames.Add(game);
        OnPropertyChanged(nameof(HasPackGames));
        PackListStatus = manifest.Packs.Count == 0 ? T("No picture packs are published yet.")
            : manifest.FailedFeeds.Count > 0 ? Plural("{0} part of the picture packs could not be read; its games are missing. JoyPro tries again in a few minutes.",
                "{0} parts of the picture packs could not be read; their games are missing. JoyPro tries again in a few minutes.", manifest.FailedFeeds.Count, manifest.FailedFeeds.Count)
            : string.Empty;
    }

    // The packs of the catalog's variants of the installed games (every game until the main window says otherwise).
    public List<CockpitPicturePack> PacksForMyAircraft(CockpitPicturePackManifest manifest)
    {
        CockpitCatalog? c = catalog ?? CockpitService.Catalog;
        List<CockpitPicturePack> packs = [];
        foreach (CockpitVariant raw in c.Aircraft.SelectMany(a => a.Variants))
        {
            CockpitVariant v = c.Resolve(raw);
            if (IsGameInstalled != null && !v.Games.Any(g => IsGameInstalled(g.Game)))
                continue;
            CockpitPicturePack? pack = manifest.For(v);
            if (pack != null && !packs.Contains(pack))
                packs.Add(pack);
        }
        return packs;
    }

    [RelayCommand]
    private async Task DownloadMyAircraftPacksAsync()
    {
        CockpitPicturePackManifest? manifest = await GetPackManifestAsync(quiet: false);
        if (manifest == null)
        {
            PackStatus = PackListStatus;
            return;
        }
        OfferMany(PacksForMyAircraft(manifest));
    }

    [RelayCommand]
    private async Task DownloadGamePacksAsync(string? game)
    {
        CockpitPicturePackManifest? manifest = await GetPackManifestAsync(quiet: false);
        if (manifest == null || string.IsNullOrWhiteSpace(game))
        {
            PackStatus = PackListStatus;
            return;
        }
        OfferMany(manifest.Packs.Where(p => string.Equals(p.Game, game, StringComparison.OrdinalIgnoreCase)).ToList());
    }

    // Several packs: asked first with their total size (only those that would change something).
    private void OfferMany(List<CockpitPicturePack> packs)
    {
        CockpitPicturePacks service = Packs();
        List<CockpitPicturePack> wanted = packs.Where(p => CockpitPicturePacks.Wanted(service.StateOf(p), false)).ToList();
        if (wanted.Count == 0)
        {
            ClearPackOffer();
            PackStatus = packs.Count == 0 ? T("No picture packs for these aircraft.") : T("The pictures of these aircraft are already on this PC.");
            return;
        }
        Offer(wanted, Plural("Download the pictures for {0} aircraft ({1} MB)?", "Download the pictures for {0} aircraft ({1} MB)?", wanted.Count, wanted.Count,
            wanted.Sum(p => p.SizeMb).ToString("0", Localization.Loc.Culture)));
    }

    public async Task DownloadPacksAsync(IReadOnlyList<CockpitPicturePack> packs)
    {
        if (packs.Count == 0 || IsDownloadingPacks)
            return;
        CockpitPicturePacks service = Packs();
        using CancellationTokenSource cts = new();
        packDownload = cts;
        IsDownloadingPacks = true;
        PackStatus = string.Empty;
        long total = Math.Max(1, packs.Sum(p => p.Size)), before = 0;
        int done = 0, current = 0;
        List<string> failures = [];
        bool cancelled = false;
        try
        {
            foreach (CockpitPicturePack pack in packs)
            {
                current++;
                long start = before;
                int index = current;
                void Show(long bytes)
                {
                    PackProgress = Math.Clamp(100.0 * (start + bytes) / total, 0, 100);
                    PackProgressText = packs.Count == 1
                        ? T("Downloading the pictures for {0}: {1} of {2} MB", pack.Set, Mb(bytes), Mb(pack.Size))
                        : T("Downloading the pictures for {0} ({1} of {2}): {3} of {4} MB", pack.Set, index, packs.Count, Mb(start + bytes), Mb(total));
                }
                Show(0);
                try
                {
                    if (await service.DownloadAsync(pack, new InlineProgress(Show), cts.Token))
                        done++;
                }
                catch (CockpitPicturePackException ex)
                {
                    failures.Add(ex.Message);
                }
                catch (Exception ex) when (ex is OperationCanceledException)
                {
                    cancelled = true;
                    break;
                }
                catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.IO.InvalidDataException)
                {
                    failures.Add(T("The pictures for {0} could not be installed: {1}", pack.Set, ex.Message));
                }
                before += pack.Size;
            }
        }
        finally
        {
            packDownload = null;
            IsDownloadingPacks = false;
            PackProgress = 0;
            PackProgressText = string.Empty;
        }
        string result = cancelled ? T("Download cancelled. The pictures downloaded before stay.")
            : Plural("Downloaded the pictures for {0} aircraft.", "Downloaded the pictures for {0} aircraft.", done, done);
        PackStatus = failures.Count == 0 ? result : result + " " + string.Join(" ", failures.Distinct());
        RefreshPicturesStatus();
        BuildPages();
    }

    // Progress on the thread that reports it (the download's continuations run on the UI thread in the app), so no
    // late update comes after the download ended.
    private sealed class InlineProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }

    private static string Mb(long bytes) => (bytes / 1048576.0).ToString("0.0", Localization.Loc.Culture);
}
