using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JoyPro.DeviceReference;
using JoyPro.Gui.Models;

namespace JoyPro.Gui.ViewModels;

// The reference as the picture canvas (DeviceMarkerCanvas, docs/ui/layout-redesign.md) binds it: the photo or JoyPro's
// drawing, the picture's size, the parts as canvas markers in picture pixels, the selection, and the canvas' events
// (a finished drag moves the part, a click places the part the marker assistant asks for, a dropped picture becomes the
// user's photo).
public sealed partial class DeviceReferenceViewModel
{
    private bool syncingCanvasSelection;

    public ObservableCollection<ReferenceCanvasMarker> CanvasMarkers { get; } = [];

    [ObservableProperty] private ReferenceCanvasMarker? selectedCanvasMarker;

    // The photo or JoyPro's line drawing (both in a photo's frame); null while a schematic drawing is shown.
    public IImage? CanvasPhoto => ShowsSchematic ? null : ShownImage;

    public DeviceSchematic? CanvasSchematic => ShowsSchematic ? ShownDrawing : null;

    // The shown picture's size (the canvas' coordinate space): the photo's or line drawing's, else the drawing's.
    public double PictureWidth => !ShowsSchematic && ShownImage != null ? ShownImage.Size.Width : ShownDrawing?.Width ?? 0;

    public double PictureHeight => !ShowsSchematic && ShownImage != null ? ShownImage.Size.Height : ShownDrawing?.Height ?? 0;

    public DeviceCanvasMode CanvasMode => IsEditable ? DeviceCanvasMode.EditMarkers : DeviceCanvasMode.View;

    private void RebuildCanvasMarkers()
    {
        foreach (ReferenceCanvasMarker old in CanvasMarkers)
            old.Detach();
        CanvasMarkers.Clear();
        double width = PictureWidth, height = PictureHeight;
        // Only parts whose position on this picture is known are drawn; the others are in the legend.
        foreach (ReferenceMarker marker in Markers.Where(m => m.IsShown))
            CanvasMarkers.Add(new ReferenceCanvasMarker(marker, width, height));
        NotifyCanvasPicture();
        SyncCanvasSelection(SelectedMarker);
    }

    private void NotifyCanvasPicture()
    {
        OnPropertyChanged(nameof(CanvasPhoto));
        OnPropertyChanged(nameof(CanvasSchematic));
        OnPropertyChanged(nameof(PictureWidth));
        OnPropertyChanged(nameof(PictureHeight));
    }

    private void SyncCanvasSelection(ReferenceMarker? marker)
    {
        syncingCanvasSelection = true;
        try
        {
            SelectedCanvasMarker = marker == null ? null : CanvasMarkers.FirstOrDefault(m => ReferenceEquals(m.Part, marker));
        }
        finally
        {
            syncingCanvasSelection = false;
        }
    }

    partial void OnSelectedCanvasMarkerChanged(ReferenceCanvasMarker? value)
    {
        if (!syncingCanvasSelection)
            SelectedMarker = value?.Part;
    }

    partial void OnIsEditableChanged(bool value) => OnPropertyChanged(nameof(CanvasMode));

    // A part's new place in picture pixels (a drag on the canvas, an arrow-key nudge, undo).
    public void MoveMarkerToPixels(ReferenceMarker marker, double x, double y)
    {
        double width = PictureWidth, height = PictureHeight;
        if (width <= 0 || height <= 0)
            return;
        MoveMarker(marker, x / width, y / height);
    }

    [RelayCommand]
    private void CanvasMarkerMoved(DeviceMarkerMove? move)
    {
        if (move?.Marker is ReferenceCanvasMarker marker && IsEditable)
            MoveMarkerToPixels(marker.Part, move.ToX, move.ToY);
    }

    // A click on the picture outside the parts: the part the marker assistant asks for goes there.
    [RelayCommand]
    private void CanvasClicked(DeviceCanvasClick? click)
    {
        if (click == null || click.IsRightButton)
            return;
        PlaceAssistantPart(click.X, click.Y);
    }

    // True when the marker assistant waited for this click (picture pixels).
    public bool PlaceAssistantPart(double x, double y)
    {
        double width = PictureWidth, height = PictureHeight;
        if (Assistant is not { IsWaitingForClick: true } assistant || width <= 0 || height <= 0)
            return false;
        return assistant.PlaceAt(System.Math.Clamp(x / width, 0, 1), System.Math.Clamp(y / height, 0, 1));
    }

    // Shows nothing (no device picked).
    public void Clear()
    {
        Assistant?.StopCommand.Execute(null);
        Assistant = null;
        ClearQuestion();
        loadedPicture = null;
        ClassSchematic = null;
        Model = null;
        BaseModel = null;
        Schematic = null;
        Photo = null;
        LineArtPicture = null;
        PhotoFile = string.Empty;
        PhotoCredit = string.Empty;
        DeviceName = string.Empty;
        Title = string.Empty;
        Subtitle = string.Empty;
        MatchReason = string.Empty;
        Status = string.Empty;
        HasUnsavedChanges = false;
        GripChoices.Clear();
        Pictures.Clear();
        SelectedPicture = null;
        RebuildMarkers();
        OnPropertyChanged(nameof(HasGripChoices));
    }

    // A picture file dropped on the canvas: the user's photo of the shown model.
    [RelayCommand]
    private async Task DropPhotoAsync(string? file)
    {
        if (!string.IsNullOrWhiteSpace(file))
            await SetUserPhotoAsync(file);
    }
}
