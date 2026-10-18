using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JoyPro.Gui.Services;

namespace JoyPro.Gui.ViewModels;

// The 3D views of the connected devices (docs/device-reference.md "3D view"): they are not shipped; when the device
// cards found a connected device whose model has a pack in JoyPro's releases, JoyPro offers it (ask), downloads it
// (always) or does nothing (never) - Settings > Privacy "Pictures from JoyPro's releases". Offline or without a pack the
// Layout page shows the flat drawing.
public sealed partial class MainWindowViewModel
{
    // Tests: another feed or install folder; null = the release and <data>\DeviceReference\Turntables.
    // Another feed starts over (its list is read again).
    internal Func<DeviceTurntablePacks> Device3DPacksFactory
    {
        get => device3DPacksFactory;
        set
        {
            device3DPacksFactory = value;
            device3DManifest = null;
            device3DManifestFailedAt = DateTime.MinValue;
        }
    }

    private Func<DeviceTurntablePacks> device3DPacksFactory = () => new DeviceTurntablePacks();

    // ask / always / never (Settings > Privacy); tests set their own.
    internal Func<string> Device3DMode { get; set; } = () => DeviceTurntablePacks.DownloadMode;

    private DeviceTurntablePackManifest? device3DManifest;
    private DateTime device3DManifestFailedAt = DateTime.MinValue;
    private readonly HashSet<string> device3DDismissed = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim device3DGate = new(1, 1);
    private List<DeviceTurntablePack> device3DOffered = [];

    // "3D views for 2 of your devices are available (3.1 MB from JoyPro's releases)."
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDevice3DOffer))]
    private string device3DOfferText = string.Empty;

    public bool HasDevice3DOffer => Device3DOfferText.Length > 0;

    [ObservableProperty] private string device3DStatus = string.Empty;

    // The connected models of the device cards: offer or download their 3D views.
    internal async Task CheckDevice3DAsync(IEnumerable<string> connectedModels, CancellationToken cancellationToken = default)
    {
        List<string> models = connectedModels.Where(m => !string.IsNullOrWhiteSpace(m)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        string mode = Device3DMode();
        if (models.Count == 0 || mode == CockpitPicturePacks.Never)
            return;
        await device3DGate.WaitAsync(cancellationToken);
        try
        {
            DeviceTurntablePacks packs = Device3DPacksFactory();
            if (device3DManifest == null)
            {
                // Not reachable: try again after a while (the next look at the devices).
                if (DateTime.UtcNow - device3DManifestFailedAt < TimeSpan.FromMinutes(10))
                    return;
                try
                {
                    device3DManifest = await packs.LoadManifestAsync(cancellationToken);
                }
                catch (DeviceTurntablePackException)
                {
                    device3DManifestFailedAt = DateTime.UtcNow;
                    return;
                }
            }
            List<DeviceTurntablePack> wanted = packs.Wanted(device3DManifest, models).Where(p => !device3DDismissed.Contains(p.Model)).ToList();
            if (wanted.Count == 0)
                return;
            if (mode == CockpitPicturePacks.Always)
            {
                await DownloadDevice3DAsync(packs, wanted, cancellationToken);
                return;
            }
            device3DOffered = wanted;
            Device3DOfferText = wanted.Count == 1
                ? T("A 3D view of {0} is available ({1:0.0} MB from JoyPro's releases on GitHub). Download it?", ModelTitle(wanted[0].Model), wanted[0].SizeMb)
                : T("3D views of {0} of your devices are available ({1:0.0} MB from JoyPro's releases on GitHub). Download them?", wanted.Count, wanted.Sum(p => p.SizeMb));
        }
        finally
        {
            device3DGate.Release();
        }
    }

    private static string ModelTitle(string model)
    {
        try
        {
            return DeviceReferenceService.Catalog.Find(model)?.Name is { Length: > 0 } name ? name : model;
        }
        catch (Exception)
        {
            return model;
        }
    }

    private async Task DownloadDevice3DAsync(DeviceTurntablePacks packs, IReadOnlyList<DeviceTurntablePack> wanted, CancellationToken cancellationToken)
    {
        int done = 0;
        List<string> failed = [];
        foreach (DeviceTurntablePack pack in wanted)
        {
            Device3DStatus = T("Downloading the 3D view of {0}...", ModelTitle(pack.Model));
            try
            {
                if (await packs.DownloadAsync(pack, null, cancellationToken))
                    done++;
            }
            catch (DeviceTurntablePackException ex)
            {
                failed.Add(ex.Message);
            }
        }
        DeviceTurntable.ClearCache();
        Device3DStatus = failed.Count > 0
            ? T("{0} 3D view(s) downloaded; {1} failed: {2}", done, failed.Count, failed[0])
            : T("{0} 3D view(s) downloaded.", done);
        StatusText = Device3DStatus;
        NotifyWorkspacePicture();
    }

    [RelayCommand]
    private async Task AcceptDevice3DAsync()
    {
        List<DeviceTurntablePack> wanted = device3DOffered;
        device3DOffered = [];
        Device3DOfferText = string.Empty;
        if (wanted.Count > 0)
            await DownloadDevice3DAsync(Device3DPacksFactory(), wanted, CancellationToken.None);
    }

    [RelayCommand]
    private void DismissDevice3D()
    {
        foreach (DeviceTurntablePack p in device3DOffered)
            device3DDismissed.Add(p.Model);
        device3DOffered = [];
        Device3DOfferText = string.Empty;
    }

    [RelayCommand]
    private async Task AlwaysDevice3DAsync()
    {
        CockpitPictureDownloadMode = CockpitPictureDownloadModes[1];
        await AcceptDevice3DAsync();
    }

    [RelayCommand]
    private void NeverDevice3D()
    {
        CockpitPictureDownloadMode = CockpitPictureDownloadModes[2];
        DismissDevice3D();
    }
}
