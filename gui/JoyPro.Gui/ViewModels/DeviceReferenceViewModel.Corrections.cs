using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JoyPro.DeviceReference;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;

namespace JoyPro.Gui.ViewModels;

// Correcting JoyPro (docs/device-reference.md): the inputs of the shown model that have no place on the picture (a
// banner offers the marker assistant for them), and "Send correction to JoyPro": the user's own positions and controls
// as a correction file plus a prefilled issue form in the browser.
public sealed partial class DeviceReferenceViewModel
{
    [ObservableProperty] private int missingMarkerCount;

    // The inputs JoyPro has no place for on this picture (while it knows the others): "press them to place them".
    public bool HasMissingMarkers => Model != null && MissingMarkerCount > 0 && KnownMarkerCount > 0 && Assistant == null;

    public string MissingMarkersText => Plural("{0} input has no place on the picture: press it on your device to place it.",
        "{0} inputs have no place on the picture: press them on your device to place them.", MissingMarkerCount);

    public string MissingMarkersList => string.Join(", ", Markers.Where(IsMissing).Select(m => m.Name).Take(8)) + (MissingMarkerCount > 8 ? ", ..." : string.Empty);

    // Parts JoyPro's set for the shown picture lists as not physical (logical buttons, a lever position).
    private HashSet<string> notPhysical = new(StringComparer.OrdinalIgnoreCase);

    private bool IsMissing(ReferenceMarker m) => !m.IsShown && m.Inputs.Any(i => i.Input.Length > 0) && !notPhysical.Contains(m.Id);

    private void UpdateMissingMarkers()
    {
        notPhysical = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Model != null && loadedPicture != null)
        {
            DeviceLayoutSet? set = DeviceReferenceService.CuratedLayouts().FirstOrDefault(s => string.Equals(s.Model, Model.Id, StringComparison.OrdinalIgnoreCase) &&
                                                                                          string.Equals(s.PictureKey, loadedPicture.Key, StringComparison.OrdinalIgnoreCase));
            if (set != null)
                notPhysical.UnionWith(set.Unplaced.Where(p => p.Value.StartsWith(DeviceCompleteness.NotPhysical, StringComparison.OrdinalIgnoreCase)).Select(p => p.Key));
        }
        MissingMarkerCount = Markers.Count(IsMissing);
        OnPropertyChanged(nameof(HasMissingMarkers));
        OnPropertyChanged(nameof(MissingMarkersText));
        OnPropertyChanged(nameof(MissingMarkersList));
    }

    partial void OnKnownMarkerCountChanged(int value) => OnPropertyChanged(nameof(HasMissingMarkers));

    // Where the correction files go: <data>\DeviceReference\Corrections.
    public static string CorrectionFolder() => Path.Combine(DeviceReferenceService.DataFolder(), "Corrections");

    // Opens the issue form (the host may replace it, the tests do).
    public Action<string> OpenUrl { get; set; } = CockpitsViewModel.OpenUrl;

    public Func<DateTime> Now { get; set; } = () => DateTime.Now;

    // The last correction file written.
    [ObservableProperty] private string lastCorrectionFile = string.Empty;

    // The user's changes on the shown picture against what JoyPro ships: placed or moved parts, added or renamed
    // controls. Null when no model or picture is shown.
    public JsonObject? BuildCorrection()
    {
        if (Model == null || loadedPicture == null)
            return null;
        RefDevice shipped = DeviceReferenceService.Catalog.Find(Model.Id) ?? Model;
        RefDevice current = CurrentDefinition() ?? Model;
        current.Id = Model.Id;
        Dictionary<string, double[]> shippedPositions = new(StringComparer.OrdinalIgnoreCase);
        DeviceLayoutSet? curated = DeviceReferenceService.CuratedLayouts().FirstOrDefault(s => string.Equals(s.Model, Model.Id, StringComparison.OrdinalIgnoreCase) &&
                                                                                             string.Equals(s.PictureKey, loadedPicture.Key, StringComparison.OrdinalIgnoreCase));
        if (curated != null)
        {
            foreach (KeyValuePair<string, double[]> pair in curated.Markers)
                shippedPositions[pair.Key] = pair.Value;
        }
        else if (loadedPicture.Kind == "schematic" && Schematic != null)
        {
            foreach (SchematicMarker m in Schematic.Markers)
                shippedPositions[m.Id] = [m.X, m.Y];
        }
        IEnumerable<DeviceCorrection.Part> placed = Markers.Where(m => m.IsPlaced).Select(m => new DeviceCorrection.Part { Id = m.Id, X = m.X, Y = m.Y });
        return DeviceCorrection.Build(shipped, current, lastProbe?.VidPid ?? string.Empty, loadedPicture.Kind, loadedPicture.Key, loadedPicture.View, placed, shippedPositions);
    }

    // "Send correction to JoyPro": saves the correction file and opens the prefilled issue form; the user decides in
    // the browser whether to submit it.
    [RelayCommand]
    private void SendCorrection()
    {
        JsonObject? correction = BuildCorrection();
        if (correction == null)
        {
            Status = T("Pick or connect a device first.");
            return;
        }
        if (DeviceCorrection.IsEmpty(correction))
        {
            Status = T("Nothing to send yet: place or move markers (Edit markers, or the marker assistant) first.");
            return;
        }
        LastCorrectionFile = DeviceCorrection.Save(correction, CorrectionFolder(), Now());
        string url = DeviceCorrection.IssueUrl(correction, out bool attach);
        OpenUrl(url);
        Status = attach
            ? T("Correction saved to {0}. The issue form is open in your browser: attach this file there and submit it if you like.", LastCorrectionFile)
            : T("Correction saved to {0}. The issue form is open in your browser with it filled in: submit it if you like.", LastCorrectionFile);
    }
}
