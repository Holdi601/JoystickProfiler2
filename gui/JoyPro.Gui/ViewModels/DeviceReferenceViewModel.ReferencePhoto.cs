using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JoyPro.DeviceReference;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;

namespace JoyPro.Gui.ViewModels;

// The reference photo of a line drawing (docs/device-reference.md, "JoyPro's line drawings"): a drawing JoyPro made
// from one of the maker's product photos lies in that photo's frame. The Layout page offers to download that photo by
// a click (never without one, and only with the consent for the device, DevicePhotoConsent); once on this PC it is a
// picture of its own ("Photo 2 · VKB"). JoyPro's positions come along when the downloaded file is the photo JoyPro
// drew from (same content hash); a photo the maker has changed since is shown as a reference without markers.
public sealed partial class DeviceReferenceViewModel
{
    // Downloads a maker's photo into the device picture cache; replaceable for tests.
    public Func<string, IProgress<double>?, Task<DeviceImageCache.DownloadResult>> DownloadReferencePhoto { get; set; } = DeviceImageCache.DownloadToCacheAsync;

    // The photo the download waits for while the consent question is open.
    private DevicePhoto? pendingReferencePhoto;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanDownloadReferencePhoto))] private bool isDownloadingReferencePhoto;
    // The consent question is open for a clicked download (asked right at the button).
    [ObservableProperty] private bool awaitsReferencePhotoConsent;
    // 0..1; -1 while the size is not known.
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ReferencePhotoProgressKnown))] private double referencePhotoProgress;
    [ObservableProperty] private string referencePhotoStatus = string.Empty;
    // The saved file of the last downloaded reference photo ("Show in folder").
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasReferencePhotoFile))] private string referencePhotoFile = string.Empty;

    public bool ReferencePhotoProgressKnown => ReferencePhotoProgress >= 0;
    public bool HasReferencePhotoFile => ReferencePhotoFile.Length > 0;

    // The button shows for a line drawing made from a maker's photo JoyPro knows the address of.
    public bool CanDownloadReferencePhoto => !IsDownloadingReferencePhoto && ReferencePhotoOf(SelectedPicture) != null;

    // The maker's photo a line drawing was drawn from, on one of the maker's own hosts; null for any other picture.
    // Found by the photo's content hash (the drawing's key), else by the same view of the model's photos.
    public DevicePhoto? ReferencePhotoOf(ReferencePictureOption? option)
    {
        if (option is not { IsLineArt: true } || Model == null || IsGeneric)
            return null;
        string key = option.Key ?? string.Empty;
        DevicePhoto? photo = Model.Photos.FirstOrDefault(p => p.Sha256.Length > 0 && string.Equals(p.Sha256, key, StringComparison.OrdinalIgnoreCase) && DevicePhotoDb.IsOfficial(Model, p.Url));
        if (photo == null && key.Length > 0)
        {
            DeviceLayoutSet? set = DeviceReferenceService.CuratedLayouts().FirstOrDefault(s =>
                string.Equals(s.Model, Model.Id, StringComparison.OrdinalIgnoreCase) && string.Equals(s.PictureKey, key, StringComparison.OrdinalIgnoreCase) &&
                s.PhotoKind == "vendor" && DevicePhotoDb.IsOfficial(Model, s.PhotoUrl));
            if (set != null)
                photo = new DevicePhoto { Model = Model.Id, Url = set.PhotoUrl, Sha256 = set.PhotoSha256, View = set.View.Length > 0 ? set.View : option.View };
        }
        string view = option.LineArt?.View ?? option.View;
        photo ??= DevicePhotoDb.OfficialPhotos(Model).FirstOrDefault(p => view.Length > 0 && string.Equals(p.View, view, StringComparison.OrdinalIgnoreCase));
        return photo;
    }

    // "Download the reference photo": with the consent for this device at once, else the consent question first (the
    // download follows a yes).
    [RelayCommand]
    private async Task DownloadReferencePhotoAsync()
    {
        DevicePhoto? photo = ReferencePhotoOf(SelectedPicture);
        if (photo == null || Model == null || IsDownloadingReferencePhoto)
            return;
        if (!DevicePhotoConsent.Allows(Model.Id))
        {
            pendingReferencePhoto = photo;
            AwaitsReferencePhotoConsent = true;
            AsksPhotoConsent = true;
            PhotoConsentText = DevicePhotoConsent.Question(Model.Name, photo.Url);
            ReferencePhotoStatus = T("Answer the question about the maker's photo: JoyPro downloads it after your yes.");
            return;
        }
        await RunReferencePhotoDownloadAsync(photo);
    }

    // The consent question was answered while a download waited for it: a yes downloads it, anything else drops it.
    // True when the answer was for a waiting download.
    private bool AnswerPendingReferencePhoto(bool allowed)
    {
        DevicePhoto? photo = pendingReferencePhoto;
        pendingReferencePhoto = null;
        AwaitsReferencePhotoConsent = false;
        if (photo == null)
            return false;
        if (allowed)
            _ = RunReferencePhotoDownloadAsync(photo);
        else
            ReferencePhotoStatus = T("Not downloaded: you said no to the maker's photos of this device.");
        return true;
    }

    private async Task RunReferencePhotoDownloadAsync(DevicePhoto photo)
    {
        int current = version;
        IsDownloadingReferencePhoto = true;
        ReferencePhotoProgress = -1;
        ReferencePhotoStatus = T("Downloading the maker's photo from {0}...", DevicePhotoConsent.SiteOf(photo.Url));
        DeviceImageCache.DownloadResult result;
        try
        {
            Progress<double> progress = new(p => ReferencePhotoProgress = p);
            result = await DownloadReferencePhoto(photo.Url, progress);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            result = new DeviceImageCache.DownloadResult(null, T("The download failed: {0}", ex.Message));
        }
        finally
        {
            IsDownloadingReferencePhoto = false;
        }
        if (current != version)
            return;
        if (result.File == null || !File.Exists(result.File))
        {
            ReferencePhotoStatus = result.Error.Length > 0 ? result.Error : T("The download failed.");
            return;
        }
        ReferencePhotoFile = result.File;
        ReferencePictureOption? option = Pictures.FirstOrDefault(p => p.Kind == "vendor" && string.Equals(p.Source, photo.Url, StringComparison.OrdinalIgnoreCase));
        if (option == null)
        {
            // Not listed (a photo found by its curated set only): a picture of its own.
            option = new ReferencePictureOption { Kind = "vendor", Title = photo.ViewTitle + " · " + VendorName(Model!), Source = photo.Url, View = photo.View, Photo = photo, Key = photo.Sha256 };
            int at = Pictures.ToList().FindIndex(p => p.Kind is "schematic" or "class");
            Pictures.Insert(at < 0 ? Pictures.Count : at, option);
        }
        if (!await SelectPictureAsync(option))
        {
            ReferencePhotoStatus = T("The photo was saved but could not be read as a picture.");
            return;
        }
        ReferencePhotoStatus = KnownMarkerCount > 0
            ? T("Saved on this PC. JoyPro's positions fit this photo.")
            : T("Saved on this PC. The maker's photo differs from the one JoyPro drew from, so it is shown as a reference without markers.");
    }

    [RelayCommand]
    private void ShowReferencePhotoInFolder()
    {
        if (!File.Exists(ReferencePhotoFile))
        {
            ReferencePhotoStatus = T("The saved photo is not there any more.");
            return;
        }
        try
        {
            ExternalProcess.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = "/select,\"" + ReferencePhotoFile + "\"", UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ReferencePhotoStatus = T("Could not open {0}: {1}", "Explorer", ex.Message);
        }
    }

    private void ResetReferencePhoto()
    {
        pendingReferencePhoto = null;
        AwaitsReferencePhotoConsent = false;
        ReferencePhotoStatus = string.Empty;
        ReferencePhotoFile = string.Empty;
        ReferencePhotoProgress = 0;
    }
}
