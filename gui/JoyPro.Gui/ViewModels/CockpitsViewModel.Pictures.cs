using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JoyPro.DeviceReference;

namespace JoyPro.Gui.ViewModels;

// The cockpit pictures made on this PC from the user's own games (docs/cockpits.md "Pictures from your games"):
// "Build pictures from my games" runs "engine cockpits pictures build" (with progress); where a variant's game has
// pictures, the manual pages show the game's own cockpit with the callouts at its controls, as the render or as JoyPro's
// line drawing of it (the user's drawing style).
public sealed partial class CockpitsViewModel
{
    public const int ModePicture = 0;
    public const int ModeDrawing = 1;

    public IReadOnlyList<string> PictureModes => [T("Picture from the game"), T("JoyPro's drawing of it")];

    // 0 the picture from the game, 1 JoyPro's line drawing of it.
    [ObservableProperty] private int pictureModeIndex = ModePicture;

    [ObservableProperty] private string picturesStatus = string.Empty;

    [ObservableProperty] private bool isBuildingPictures;

    // Runs "engine cockpits pictures build --game all"; returns its output (null = no engine). Set by the main window.
    public Func<Task<string?>>? PictureBuilder { get; set; }

    private string picturesRoot = CockpitPictureSet.Root();

    // Where the pictures are (tests set it); default <data>\GameData\CockpitPictures. A new place shows its pictures.
    public string PicturesRoot
    {
        get => picturesRoot;
        set
        {
            if (string.Equals(picturesRoot, value, StringComparison.OrdinalIgnoreCase))
                return;
            picturesRoot = value;
            RefreshPicturesStatus();
            if (Variant != null)
                BuildPages();
        }
    }

    // The pictures of the variant shown, or null.
    public CockpitPictureSet? GamePictures { get; private set; }

    public bool HasGamePictures => GamePictures != null;

    public string GamePicturesText => GamePictures == null ? string.Empty
        : GamePictures.Built.Length >= 10
            ? T("Pictures from {0} on this PC ({1}, made {2})", CockpitManualPages.GameName(GamePictures.Game), GamePictures.Plane, GamePictures.Built.Substring(0, 10))
            : T("Pictures from {0} on this PC ({1})", CockpitManualPages.GameName(GamePictures.Game), GamePictures.Plane);

    private string PictureModeKey => PictureModeIndex == ModeDrawing ? "drawing" : "picture";

    partial void OnPictureModeIndexChanged(int value) => BuildPages();

    // The set for the variant (looked up again on every page build: a build may have made it meanwhile).
    private CockpitPictureSet? FindGamePictures(CockpitVariant? v)
    {
        CockpitPictureSet? set = null;
        try
        {
            set = v == null ? null : CockpitPictureSet.For(v, PicturesRoot);
        }
        catch (Exception)
        {
            set = null;
        }
        if (!ReferenceEquals(set, GamePictures))
        {
            GamePictures = set;
            OnPropertyChanged(nameof(GamePictures));
            OnPropertyChanged(nameof(HasGamePictures));
            OnPropertyChanged(nameof(GamePicturesText));
        }
        return set;
    }

    // After a language change: the texts made from getters are read again.
    public void RefreshTextsForLanguage() => OnPropertyChanged(string.Empty);

    public void RefreshPicturesStatus()
    {
        List<CockpitPictureSet.GameStatus> status;
        try
        {
            status = CockpitPictureSet.Status(PicturesRoot);
        }
        catch (Exception)
        {
            status = [];
        }
        PicturesStatus = status.Count == 0
            ? T("No pictures from your games yet. JoyPro can make them from the cockpits of DCS World and X-Plane 12 on this PC.")
            : T("Pictures from your games: {0} (made {1}).", string.Join(", ", status.Select(s => Plural("{1} {0} cockpit", "{1} {0} cockpits", s.Sets, s.Sets, CockpitManualPages.GameName(s.Game)))), status.Max(s => s.Built));
    }

    [RelayCommand]
    private async Task BuildPicturesAsync()
    {
        if (PictureBuilder == null || IsBuildingPictures)
            return;
        IsBuildingPictures = true;
        PicturesStatus = T("Making the pictures from your games (DCS World, X-Plane 12)... This takes a few minutes per game.");
        try
        {
            string? output = await PictureBuilder();
            RefreshPicturesStatus();
            if (output == null)
                PicturesStatus = T("The engine could not be started: {0}", PicturesStatus);
        }
        catch (Exception ex)
        {
            PicturesStatus = T("Making the pictures failed: {0}", ex.Message);
        }
        finally
        {
            IsBuildingPictures = false;
            BuildPages();
        }
    }
}
