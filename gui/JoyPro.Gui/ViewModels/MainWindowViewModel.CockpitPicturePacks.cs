using JoyPro.Gui.Services;

namespace JoyPro.Gui.ViewModels;

// Settings > Privacy: the picture packs of JoyPro's releases on the Cockpits page (docs/cockpits.md "Picture packs"):
// ask before a download (default), download without asking, or never look.
public sealed partial class MainWindowViewModel
{
    // English values; the combo box shows them translated (TConverter).
    public string[] CockpitPictureDownloadModes { get; } = [N("Ask"), N("Always download"), N("Never")];

    public string CockpitPictureDownloadMode
    {
        get => CockpitPicturePacks.DownloadMode switch
        {
            CockpitPicturePacks.Always => CockpitPictureDownloadModes[1],
            CockpitPicturePacks.Never => CockpitPictureDownloadModes[2],
            _ => CockpitPictureDownloadModes[0]
        };
        set
        {
            string mode = value == CockpitPictureDownloadModes[1] ? CockpitPicturePacks.Always : value == CockpitPictureDownloadModes[2] ? CockpitPicturePacks.Never : CockpitPicturePacks.Ask;
            if (mode == CockpitPicturePacks.DownloadMode)
                return;
            CockpitPicturePacks.DownloadMode = mode;
            OnPropertyChanged();
        }
    }
}
