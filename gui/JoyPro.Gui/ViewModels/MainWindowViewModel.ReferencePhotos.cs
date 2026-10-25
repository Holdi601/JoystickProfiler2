using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JoyPro.DeviceReference;
using JoyPro.Gui.Services;

namespace JoyPro.Gui.ViewModels;

// The reference photos of the connected devices (docs/device-reference.md "Reference photos on this PC"): when a device
// appears whose photos are not on this PC yet, JoyPro offers them (Start page, Layout page). One click downloads them;
// "Always download" (Settings > Privacy "Download reference photos automatically") does it in the background from then
// on. The download runs in the background with a progress bar; nothing waits for it.
public sealed partial class MainWindowViewModel
{
    // Tests: their own store (fake server, temp folder).
    internal Func<ReferencePhotoStore> ReferencePhotoStoreFactory { get; set; } = () => ReferencePhotoStore.Default;

    private readonly HashSet<string> referencePhotosDismissed = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim referencePhotosGate = new(1, 1);
    private List<RefDevice> referencePhotosOffered = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReferencePhotoOffer))]
    private string referencePhotoOfferText = string.Empty;

    public bool HasReferencePhotoOffer => ReferencePhotoOfferText.Length > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReferencePhotoProgressPercent))]
    private double referencePhotoDownloadProgress;

    [ObservableProperty] private bool isDownloadingReferencePhotos;
    [ObservableProperty] private string referencePhotoDownloadStatus = string.Empty;

    public double ReferencePhotoProgressPercent => ReferencePhotoDownloadProgress * 100;

    // Settings > Privacy: "Download reference photos automatically" (kept in <data>\DeviceReference\settings.json).
    public bool AutoDownloadReferencePhotos
    {
        get => DevicePhotoConsent.AutoDownload;
        set
        {
            if (value == DevicePhotoConsent.AutoDownload)
                return;
            DevicePhotoConsent.AutoDownload = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PhotoConsentMode));
            if (value)
                _ = CheckReferencePhotosAsync(DeviceCards.Where(c => c.Connected).Select(c => c.ModelId).ToList());
        }
    }

    // Tests: another feed and folder for the photo list.
    internal Func<DevicePhotoList> DevicePhotoListFactory { get; set; } = () => new DevicePhotoList();

    // The photo list in JoyPro's releases: at most once a day, in the background; a newer valid list is used from then
    // on (the catalog is read again). Nothing is said: whatever fails, the list on this PC stays.
    internal async Task<DevicePhotoList.Outcome> CheckDevicePhotoListAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        if (DevicePhotoConsent.Mode == DevicePhotoConsent.Never)
            return DevicePhotoList.Outcome.Off;
        DevicePhotoList list = DevicePhotoListFactory();
        // Online only when pictures from the internet are allowed (the tests block them; a local feed is fine).
        bool localFeed = DefaultUpdateTransport.LocalPath(list.Feed) != null;
        if (!localFeed && (!DeviceImageCache.InternetPicturesEnabled || DeviceImageCache.InternetPicturesBlocked))
            return DevicePhotoList.Outcome.Off;
        if (!force && !list.IsDue(DateTime.UtcNow))
            return DevicePhotoList.Outcome.NotDue;
        DevicePhotoList.Outcome outcome;
        try
        {
            DeviceCatalog catalog = DeviceReferenceService.Catalog;
            string shipped = DevicePhotoDb.PhotoFolderOf(DeviceReferenceService.CatalogFolder());
            outcome = await Task.Run(() => list.CheckAsync(catalog, shipped, force, cancellationToken), cancellationToken);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return DevicePhotoList.Outcome.Rejected;
        }
        if (outcome == DevicePhotoList.Outcome.Installed)
        {
            DeviceReferenceService.Reload();
            _ = LoadDeviceCardPicturesAsync(lastCardPictures);
        }
        return outcome;
    }

    // The connected models of the device cards: offer their reference photos, or download them (automatic setting).
    internal async Task CheckReferencePhotosAsync(IEnumerable<string> connectedModels, CancellationToken cancellationToken = default)
    {
        if (DevicePhotoConsent.Mode == DevicePhotoConsent.Never)
            return;
        ReferencePhotoStore store = ReferencePhotoStoreFactory();
        if (!store.CanDownload)
            return;
        // A newer photo list first (repaired addresses), at most once a day.
        await CheckDevicePhotoListAsync(cancellationToken: cancellationToken);
        List<RefDevice> wanted = [];
        foreach (string id in connectedModels.Where(m => !string.IsNullOrWhiteSpace(m)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            RefDevice? model;
            try
            {
                model = DeviceReferenceService.Catalog.Find(id);
            }
            catch (Exception)
            {
                continue;
            }
            // Said no for this device, or not now in this session: not offered again.
            if (model == null || referencePhotosDismissed.Contains(model.Id) || DevicePhotoConsent.For(model.Id) == PhotoConsentState.Denied)
                continue;
            if (store.Missing(model).Count > 0)
                wanted.Add(model);
        }
        if (wanted.Count == 0)
            return;
        if (DevicePhotoConsent.AutoDownload)
        {
            await DownloadReferencePhotosAsync(store, wanted, cancellationToken);
            return;
        }
        await referencePhotosGate.WaitAsync(cancellationToken);
        try
        {
            referencePhotosOffered = wanted;
            int photos = wanted.Sum(m => store.Missing(m).Count);
            ReferencePhotoOfferText = wanted.Count == 1
                ? Plural("JoyPro knows {0} reference photo of your {1} ({2}). Download it? It is checked and stored on this PC only.",
                    "JoyPro knows {0} reference photos of your {1} ({2}). Download them? They are checked and stored on this PC only.",
                    photos, photos, wanted[0].Name, SitesOf(store.Missing(wanted[0])))
                : T("JoyPro knows {0} reference photos of {1} of your devices. Download them? They are checked and stored on this PC only.", photos, wanted.Count);
        }
        finally
        {
            referencePhotosGate.Release();
        }
    }

    // The sites the photos come from as people know them: the page that shows the photo (a maker's CDN address says
    // nothing), the web archive for a photo only it still has.
    private static string SitesOf(IEnumerable<DevicePhoto> photos) =>
        string.Join(", ", photos.Select(p => DevicePhotoConsent.SiteOf(p.ArchiveOnly ? "https://web.archive.org" : p.Page.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? p.Page : p.Url))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(3));

    // Downloads every missing photo of the models in the background: progress over all photos, the device cards and
    // the Layout page show them as soon as they are there.
    private async Task DownloadReferencePhotosAsync(ReferencePhotoStore store, IReadOnlyList<RefDevice> models, CancellationToken cancellationToken)
    {
        if (IsDownloadingReferencePhotos)
            return;
        int total = models.Sum(m => store.Missing(m).Count);
        if (total == 0)
            return;
        IsDownloadingReferencePhotos = true;
        ReferencePhotoDownloadProgress = 0;
        int done = 0, downloaded = 0, archived = 0;
        List<string> failed = [];
        try
        {
            foreach (RefDevice model in models)
            {
                foreach (DevicePhoto photo in store.Missing(model))
                {
                    ReferencePhotoDownloadStatus = T("Downloading reference photos: {0} of {1} ({2})...", done + 1, total, model.Name);
                    ReferencePhotoStore.PhotoResult result;
                    try
                    {
                        result = await Task.Run(() => store.EnsureAsync(model.Id, photo, null, cancellationToken), cancellationToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        result = new ReferencePhotoStore.PhotoResult(null, string.Empty, false, ex.Message);
                    }
                    if (result.File != null)
                    {
                        downloaded++;
                        if (result.Origin == "archive")
                            archived++;
                    }
                    else
                        failed.Add(model.Name + " · " + photo.ViewTitle + ": " + result.Error);
                    done++;
                    ReferencePhotoDownloadProgress = done / (double)total;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            IsDownloadingReferencePhotos = false;
        }
        string text = failed.Count == 0
            ? Plural("{0} reference photo downloaded and checked.", "{0} reference photos downloaded and checked.", downloaded)
            : T("{0} reference photos downloaded; {1} could not be downloaded (also not from the web archive): {2}", downloaded, failed.Count, failed[0]);
        if (archived > 0)
            text += " " + Plural("{0} came from the web archive because the original address no longer works.", "{0} came from the web archive because the original addresses no longer work.", archived);
        ReferencePhotoDownloadStatus = text;
        StatusText = text;
        _ = LoadDeviceCardPicturesAsync(lastCardPictures);
        NotifyWorkspacePicture();
    }

    [RelayCommand]
    private async Task AcceptReferencePhotosAsync()
    {
        List<RefDevice> wanted = referencePhotosOffered;
        referencePhotosOffered = [];
        ReferencePhotoOfferText = string.Empty;
        // The click is the yes for these devices (DevicePhotoConsent), so the photos are also shown.
        foreach (RefDevice model in wanted)
        {
            if (!DevicePhotoConsent.Allows(model.Id))
                DevicePhotoConsent.Answer(model.Id, "yes");
        }
        if (wanted.Count > 0)
            await DownloadReferencePhotosAsync(ReferencePhotoStoreFactory(), wanted, CancellationToken.None);
    }

    [RelayCommand]
    private void DismissReferencePhotos()
    {
        foreach (RefDevice model in referencePhotosOffered)
            referencePhotosDismissed.Add(model.Id);
        referencePhotosOffered = [];
        ReferencePhotoOfferText = string.Empty;
    }

    [RelayCommand]
    private async Task AlwaysReferencePhotosAsync()
    {
        DevicePhotoConsent.AutoDownload = true;
        OnPropertyChanged(nameof(AutoDownloadReferencePhotos));
        OnPropertyChanged(nameof(PhotoConsentMode));
        await AcceptReferencePhotosAsync();
    }

    [RelayCommand]
    private void NeverReferencePhotos()
    {
        foreach (RefDevice model in referencePhotosOffered)
            DevicePhotoConsent.Answer(model.Id, "no");
        referencePhotosOffered = [];
        ReferencePhotoOfferText = string.Empty;
        OnPropertyChanged(nameof(PhotoConsentSummary));
    }
}
