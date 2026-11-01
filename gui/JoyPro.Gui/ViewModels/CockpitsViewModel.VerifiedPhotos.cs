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

// Photos of cockpits no game has (docs/cockpits.md "Photos of cockpits no game has"): for an aircraft without good
// pictures from a game, JoyPro knows photo addresses with a SHA-256 each. "Download photos" (one click: the yes for this
// aircraft) downloads all of them to <data>\CockpitPhotos (ReferencePhotoStore: the original address, checked against
// the SHA-256, else the Wayback snapshot), with progress and Cancel; the manual then shows a page per photo with JoyPro's
// labels drawn on top (CockpitManualPages.PhotoPage). Photos already on this PC are shown without asking again.
public sealed partial class CockpitsViewModel
{
    private CancellationTokenSource? photoDownload;

    // Where the verified cockpit photos are kept (tests: a temp folder and a fake server through its Fetch).
    public ReferencePhotoStore PhotoStore { get; set; } = new(CockpitPhotos.StoreFolder());

    [ObservableProperty] private bool isDownloadingPhotos;
    [ObservableProperty] private double photoProgress;
    [ObservableProperty] private string photoProgressText = string.Empty;
    [ObservableProperty] private string photoDownloadStatus = string.Empty;

    // The download under way (tests wait for it).
    public Task PhotoDownload { get; private set; } = Task.CompletedTask;

    // The verified photos of the variant shown (none where a game has good pictures of it: they are not needed there).
    public List<CockpitPhoto> VerifiedPhotos
    {
        get
        {
            CockpitVariant? v = Variant;
            if (v == null || (GamePictures != null && !GamePictures.LowQuality))
                return [];
            return CockpitPhotos.Of(v);
        }
    }

    public List<CockpitPhoto> MissingPhotos => VerifiedPhotos.Where(p => PhotoStore.FileOf(p.Url) == null).ToList();

    public bool HasVerifiedPhotos => VerifiedPhotos.Count > 0;

    // "Download photos" is offered while a verified photo of the variant is not on this PC.
    public bool HasPhotoOffer => !IsDownloadingPhotos && MissingPhotos.Count > 0;

    public string PhotoOfferText
    {
        get
        {
            List<CockpitPhoto> all = VerifiedPhotos;
            if (all.Count == 0 || Aircraft == null)
                return string.Empty;
            List<string> sites = all.Select(p => DevicePhotoConsent.SiteOf(p.Page.Length > 0 ? p.Page : p.Url)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            string first = GamePictures != null ? T("The pictures from the game are poor for this cockpit.") : T("No game has pictures of this cockpit.");
            return first + " " + T("Photos of the {0} cockpit JoyPro knows: {1} ({2}), with JoyPro's labels on top. They are downloaded to this PC only.",
                Aircraft.Name, all.Count, string.Join(", ", sites));
        }
    }

    private void RefreshPhotoOffer()
    {
        foreach (string name in new[] { nameof(VerifiedPhotos), nameof(MissingPhotos), nameof(HasVerifiedPhotos), nameof(HasPhotoOffer), nameof(PhotoOfferText) })
            OnPropertyChanged(name);
    }

    partial void OnIsDownloadingPhotosChanged(bool value) => OnPropertyChanged(nameof(HasPhotoOffer));

    // The photo as the store's DevicePhoto (same download, hash check and Wayback fallback as a device reference photo).
    public static DevicePhoto AsDevicePhoto(CockpitAircraft? aircraft, CockpitPhoto p) => new()
    {
        Model = aircraft?.Id ?? string.Empty,
        Url = p.Url,
        View = p.View.Length > 0 ? p.View : "cockpit",
        Page = p.Page,
        Sha256 = p.Sha256,
        Sha256Alt = p.Sha256Alt.ToList(),
        Archive = p.Archive,
        Licence = p.License,
        Source = p.Source.Length > 0 ? p.Source : "commons",
        Width = p.Width,
        Height = p.Height
    };

    [RelayCommand]
    private void DownloadPhotos()
    {
        if (IsDownloadingPhotos || Aircraft == null)
            return;
        PhotoDownload = DownloadPhotosAsync();
    }

    [RelayCommand]
    private void CancelPhotoDownload() => photoDownload?.Cancel();

    private async Task DownloadPhotosAsync()
    {
        CockpitAircraft? aircraft = Aircraft;
        List<CockpitPhoto> photos = MissingPhotos;
        if (aircraft == null || photos.Count == 0)
            return;
        if (!PhotoStore.CanDownload)
        {
            PhotoDownloadStatus = T("Pictures from the internet are switched off (Settings > Privacy).");
            return;
        }
        // The click is the yes for this aircraft (also for its other photo links below).
        CockpitPhotoConsent.Answer(aircraft.Id, "yes");
        OnPropertyChanged(nameof(AskPhotos));
        OnPropertyChanged(nameof(PhotosDenied));
        photoDownload = new CancellationTokenSource();
        CancellationToken cancel = photoDownload.Token;
        IsDownloadingPhotos = true;
        PhotoDownloadStatus = string.Empty;
        int done = 0, fromArchive = 0, changed = 0;
        List<string> failed = [];
        try
        {
            for (int i = 0; i < photos.Count; i++)
            {
                cancel.ThrowIfCancellationRequested();
                CockpitPhoto photo = photos[i];
                int at = i;
                PhotoProgress = 100.0 * i / photos.Count;
                PhotoProgressText = T("Downloading photo {0} of {1}...", i + 1, photos.Count);
                Progress<double> progress = new(f => PhotoProgress = 100.0 * (at + Math.Clamp(f, 0, 1)) / photos.Count);
                ReferencePhotoStore.PhotoResult result = await PhotoStore.EnsureAsync(aircraft.Id, AsDevicePhoto(aircraft, photo), progress, cancel);
                if (result.File == null)
                {
                    failed.Add(CockpitManualPages.PhotoTitle(photo) + ": " + result.Error);
                    continue;
                }
                done++;
                if (result.Origin == "archive")
                    fromArchive++;
                // Other bytes than on record: only a photo of another size can put the labels off (a thumbnail the site
                // made again, with the same size, keeps them where they belong).
                int[] size = result.Changed ? CockpitPhotos.ImageSize(result.File) : [photo.Width, photo.Height];
                if (result.Changed && (size[0] != photo.Width || size[1] != photo.Height))
                    changed++;
            }
            PhotoProgress = 100;
            List<string> parts = [T("Photos downloaded and checked: {0}.", done)];
            if (fromArchive > 0)
                parts.Add(T("From the web archive's copy (no longer on their site): {0}.", fromArchive));
            if (changed > 0)
                parts.Add(T("Changed on their site since JoyPro recorded them (the labels may not fit): {0}.", changed));
            if (failed.Count > 0)
                parts.Add(T("Not downloaded: {0}", string.Join("; ", failed)));
            PhotoDownloadStatus = string.Join(" ", parts);
        }
        catch (OperationCanceledException)
        {
            PhotoDownloadStatus = T("Cancelled. Photos downloaded: {0}.", done);
        }
        finally
        {
            IsDownloadingPhotos = false;
            photoDownload?.Dispose();
            photoDownload = null;
        }
        if (aircraft != Aircraft)
            return;
        RefreshPhotoOffer();
        BuildPages();
        // The first photo page right away.
        ManualPage? first = Pages.Select(p => p.Page).FirstOrDefault(p => p.Kind == "photo");
        if (first != null && done > 0)
            OpenPage(first.Id);
        Photos.Clear();
        PhotoLoad = LoadPhotosAsync();
    }
}
