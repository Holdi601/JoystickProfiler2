using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JoyPro.DeviceReference;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;

namespace JoyPro.Gui.ViewModels;

// Tables and lines on the Layout page (docs/device-reference.md, "Tables and lines"; docs/ui/layout-redesign.md): a
// reference picture with known positions is shown as a callout sheet like JoyPro 1's cheat sheets: a table per control
// group around the picture, orthogonal lines to the parts. The sheet is arranged automatically, edited on the canvas
// (CalloutSheetCanvas, CalloutSheetViewModel), kept per picture in <data>\DeviceReference\Callouts (joypro-device-layout
// with "callouts"), and the cheat sheets render that file through "engine layout render". A relation dropped on a row
// binds it; a preview shows a game's own menu titles for one aircraft.
public sealed partial class MainWindowViewModel
{
    public const string RelationNamesPreview = "Relation names";

    private CalloutSheetViewModel? layoutCallouts;
    private string calloutPictureKey = string.Empty;
    private string calloutModel = string.Empty;
    private string calloutTitlesDevice = string.Empty;
    // Game | plane -> token ("JOY_BTN5", "Modifier+JOY_BTN5") -> the game's menu title (engine layout titles).
    private readonly Dictionary<string, Dictionary<string, string>> calloutTitles = new(StringComparer.OrdinalIgnoreCase);
    private string calloutTitlesModifier = string.Empty;

    public CalloutSheetViewModel LayoutCallouts => layoutCallouts ??= CreateLayoutCallouts();

    // The previews of the sheet: relation names, or one game and aircraft (its own menu titles).
    public ObservableCollection<string> CalloutPreviewOptions { get; } = [RelationNamesPreview];

    // Which modifier combinations get a column: the ones in use (default), all of them, or one picked combination.
    public ObservableCollection<string> CalloutModifierOptions { get; } = [];

    // How many columns a table has at most (the unmodified one included).
    public ObservableCollection<int> CalloutMaxColumnsOptions { get; } = [2, 3, 4, 6, 8, 12, 16];

    // Tables and lines (on) or the markers with labels (off) on a reference picture.
    [ObservableProperty]
    private bool layoutCalloutsEnabled = true;

    [ObservableProperty]
    private string selectedCalloutPreview = RelationNamesPreview;

    public bool LayoutShowsCallouts =>
        LayoutShowsReference && LayoutCalloutsEnabled && deviceReference?.HasModel == true && deviceReference.KnownMarkerCount > 0 &&
        deviceReference.Assistant == null && LayoutCallouts.HasSheet;

    public bool LayoutShowsMarkerCanvas => !LayoutShowsCallouts;

    // The tables toggle is offered where a sheet is possible.
    public bool CanShowLayoutCallouts => LayoutShowsReference && deviceReference?.HasModel == true && deviceReference.KnownMarkerCount > 0;

    public bool IsCalloutEditMode => LayoutShowsCallouts && IsLayoutEditMode;

    // The preview box (relation names / a game's menu titles) outside Edit mode, where the toolbar has the edit tools.
    public bool ShowCalloutPreviewBox => LayoutShowsCallouts && !IsLayoutEditMode;

    public string? CalloutHeaderGame => IsPlanePreview(out string game, out _) ? game : null;

    public string? CalloutHeaderPlane => IsPlanePreview(out _, out string plane) ? plane : null;

    // The sheet's modifier columns: the combinations in use (AutoModifierChoice), all combinations of the profile's
    // modifiers (AllCombinationsChoice), or one picked modifier combination alone.
    public string CalloutModifier
    {
        get
        {
            CalloutSheet? sheet = LayoutCallouts.Sheet;
            if (sheet == null)
                return AutoModifierChoice;
            if (sheet.Modifier.Length > 0)
                return CalloutCombos.Label(sheet.Modifier);
            return string.Equals(sheet.ComboMode, CalloutCombos.All, StringComparison.OrdinalIgnoreCase) ? AllCombinationsChoice : AutoModifierChoice;
        }
        set
        {
            CalloutSheet? sheet = LayoutCallouts.Sheet;
            string next = (value ?? string.Empty).Trim();
            if (sheet == null || next.Length == 0)
                return;
            string mode = string.Empty, picked = string.Empty;
            if (next == AllCombinationsChoice)
                mode = CalloutCombos.All;
            else if (next != AutoModifierChoice)
                picked = CalloutCombos.Key(CalloutCombos.Split(next.Replace(" + ", "+")));
            if (string.Equals(sheet.Modifier, picked, StringComparison.Ordinal) && string.Equals(sheet.ComboMode, mode, StringComparison.Ordinal))
                return;
            sheet.Modifier = picked;
            sheet.ComboMode = mode;
            LayoutCallouts.HasUnsavedChanges = true;
            ApplyCalloutCombos();
            LayoutCallouts.Refresh();
            OnPropertyChanged();
            OnPropertyChanged(nameof(CalloutModifierInUse));
        }
    }

    // Columns of a table at most (the unmodified one included); default 8 = every combination of 3 modifiers.
    public int CalloutMaxColumns
    {
        get => LayoutCallouts.Sheet is { MaxColumns: > 0 } sheet ? sheet.MaxColumns : CalloutCombos.DefaultMaxColumns;
        set
        {
            CalloutSheet? sheet = LayoutCallouts.Sheet;
            int next = Math.Clamp(value, 2, 64);
            if (sheet == null || sheet.MaxColumns == next || (sheet.MaxColumns == 0 && next == CalloutCombos.DefaultMaxColumns))
                return;
            sheet.MaxColumns = next;
            LayoutCallouts.HasUnsavedChanges = true;
            ApplyCalloutCombos();
            LayoutCallouts.Refresh();
            OnPropertyChanged();
            OnPropertyChanged(nameof(CalloutModifierInUse));
        }
    }

    public const string AutoModifierChoice = "(combinations in use)";
    public const string AllCombinationsChoice = "(all combinations)";

    public string CalloutModifierInUse
    {
        get
        {
            CalloutSheet? sheet = layoutCallouts?.Sheet;
            if (sheet != null && sheet.Combos.Count > 0)
                return T("Columns: {0}.", string.Join(" | ", new[] { CalloutCombos.Label(string.Empty) }.Concat(sheet.Combos.Select(CalloutCombos.Label))));
            string m = ResolvedCalloutModifier();
            return m.Length == 0 ? T("No modifier binds on this device yet.") : T("Second column: binds with {0}.", m);
        }
    }

    // The modifier combinations of every bind of the shown device (one entry per bind, "" = none).
    private List<string> DeviceBindCombos(string device) =>
        device.Length == 0
            ? []
            : allBinds.Where(b => b.HasBind && (SameDevice(b.Device, device) || SameDeviceName(b.Device, device)))
                .Select(b => CalloutCombos.Key(CalloutCombos.Split(b.ModifiersSummary)))
                .ToList();

    // The columns of the shown sheet follow the binds and the sheet's choice (CalloutCombos.Choose); a profile without
    // modifier binds keeps the single modifier column of before.
    private void ApplyCalloutCombos()
    {
        CalloutSheet? sheet = layoutCallouts?.Sheet;
        if (sheet == null)
            return;
        List<string> combos = CalloutCombos.Choose(sheet.ComboMode, ModifierNames, DeviceBindCombos(ResolveLayoutEditorContextDevice()), sheet.MaxColumns, sheet.Modifier);
        if (LayoutCallouts.SetCombos(combos))
            Log("[Callouts] columns: " + (combos.Count == 0 ? "(one modifier column)" : string.Join(" | ", combos.Select(CalloutCombos.Label))));
        OnPropertyChanged(nameof(CalloutModifierInUse));
    }

    private CalloutSheetViewModel CreateLayoutCallouts()
    {
        CalloutSheetViewModel callouts = new() { TextOfColumn = CalloutRowText };
        callouts.BindRequested += (_, request) => _ = BindCalloutRowAsync(request);
        callouts.PartPlaced += (_, placed) => OnCalloutPartPlaced(placed.Part, placed.X, placed.Y);
        callouts.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(CalloutSheetViewModel.HasUnsavedChanges):
                    OnPropertyChanged(nameof(WorkspaceHasUnsavedChanges));
                    OnPropertyChanged(nameof(CanSaveWorkspace));
                    break;
                case nameof(CalloutSheetViewModel.CanUndo):
                case nameof(CalloutSheetViewModel.CanRedo):
                    NotifyLayoutUndoState();
                    break;
                case nameof(CalloutSheetViewModel.Status):
                    if (callouts.Status.Length > 0)
                        LayoutEditorStatus = callouts.Status;
                    break;
                case nameof(CalloutSheetViewModel.HasSheet):
                    NotifyCallouts();
                    break;
            }
        };
        return callouts;
    }

    private void NotifyCallouts()
    {
        OnPropertyChanged(nameof(LayoutShowsCallouts));
        OnPropertyChanged(nameof(LayoutShowsMarkerCanvas));
        OnPropertyChanged(nameof(CanShowLayoutCallouts));
        OnPropertyChanged(nameof(IsCalloutEditMode));
        OnPropertyChanged(nameof(ShowCalloutPreviewBox));
        OnPropertyChanged(nameof(CalloutModifier));
        OnPropertyChanged(nameof(CalloutMaxColumns));
        OnPropertyChanged(nameof(CalloutModifierInUse));
        OnPropertyChanged(nameof(WorkspaceHasUnsavedChanges));
        OnPropertyChanged(nameof(CanSaveWorkspace));
        NotifyLayoutUndoState();
    }

    partial void OnLayoutCalloutsEnabledChanged(bool value) => NotifyCallouts();

    partial void OnSelectedCalloutPreviewChanged(string value)
    {
        OnPropertyChanged(nameof(CalloutHeaderGame));
        OnPropertyChanged(nameof(CalloutHeaderPlane));
        LayoutCallouts.Refresh();
    }

    [RelayCommand]
    private void ToggleLayoutCallouts() => LayoutCalloutsEnabled = !LayoutCalloutsEnabled;

    // ------------------------------------------------------------------ the sheet of the shown picture

    // The positions of the parts on the shown picture (only the known ones).
    private Dictionary<string, double[]> KnownReferencePositions() =>
        deviceReference == null
            ? new(StringComparer.OrdinalIgnoreCase)
            : deviceReference.Markers.Where(m => m.IsShown).ToDictionary(m => m.Id, m => new[] { m.X, m.Y }, StringComparer.OrdinalIgnoreCase);

    // The sheet's file key: a photo's content hash; JoyPro's line drawing of a photo has a sheet of its own (its cheat
    // sheets show the drawing, not the photo), in the same frame.
    private string CalloutPictureKeyOf(ReferencePictureOption? picture) =>
        picture == null ? string.Empty : picture.IsPhoto ? picture.Key : picture.IsLineArt && picture.Key.Length > 0 ? "lineart-" + picture.Key : picture.Kind;

    // After the reference's picture or markers changed: the sheet of that picture (saved, or arranged now and kept), or
    // the spots follow the moved parts.
    private void RefreshLayoutCallouts()
    {
        DeviceReferenceViewModel? reference = deviceReference;
        if (reference == null)
            return;
        reference.CurrentCallouts ??= () => LayoutCallouts.Sheet;
        ReferencePictureOption? picture = reference.LoadedPicture;
        string model = reference.Model?.Id ?? string.Empty;
        string key = CalloutPictureKeyOf(picture);
        Dictionary<string, double[]> known = KnownReferencePositions();
        if (model.Length == 0 || key.Length == 0 || known.Count == 0 || reference.Schematic == null || reference.PictureWidth <= 0)
        {
            if (LayoutCallouts.HasSheet && (model != calloutModel || key != calloutPictureKey))
            {
                calloutModel = model;
                calloutPictureKey = key;
                LayoutCallouts.Show(null, known, 0, 0, null);
            }
            NotifyCallouts();
            return;
        }
        if (model != calloutModel || key != calloutPictureKey || !LayoutCallouts.HasSheet)
        {
            calloutModel = model;
            calloutPictureKey = key;
            DeviceLayoutSet? saved = DeviceReferenceService.LoadCallouts(model, key);
            // Without a sheet of the user's own: JoyPro's shipped sheet for this picture, else a fresh arrangement.
            CalloutSheet? sheet = saved?.Callouts ?? DeviceReferenceService.CuratedCallouts(model, picture?.HasPhotoFrame == true ? picture.Key : key);
            LayoutCallouts.Show(reference.Schematic, known, reference.PictureWidth, reference.PictureHeight, sheet);
            // One column per modifier combination in use (a changed set of combinations arranges the sheet again).
            bool combosChanged = LayoutCallouts.Sheet != null && LayoutCallouts.SetCombos(CalloutCombos.Choose(LayoutCallouts.Sheet.ComboMode, ModifierNames,
                DeviceBindCombos(ResolveLayoutEditorContextDevice()), LayoutCallouts.Sheet.MaxColumns, LayoutCallouts.Sheet.Modifier));
            // The arranged sheet is kept at once (the cheat sheets use it); the user's edits need Save.
            if (saved?.Callouts == null || combosChanged)
                SaveLayoutCalloutsFile(markSaved: true);
            else if (reference.DeviceName.Length > 0)
                DeviceReferenceService.SetCalloutFileOf(reference.DeviceName, saved.File);
            RebuildCalloutModifierOptions();
            RebuildCalloutRelationItems();
            _ = LoadCalloutTitlesAsync();
        }
        else
        {
            LayoutCallouts.UpdatePositions(known);
        }
        NotifyCallouts();
    }

    // Writes the sheet of the shown picture (with the positions and, for a photo, a local PNG copy the engine can read).
    private string SaveLayoutCalloutsFile(bool markSaved)
    {
        DeviceReferenceViewModel? reference = deviceReference;
        CalloutSheet? sheet = LayoutCallouts.Sheet;
        if (reference?.Model == null || sheet == null || calloutPictureKey.Length == 0)
            return string.Empty;
        ReferencePictureOption? picture = reference.LoadedPicture;
        DeviceLayoutSet set = new()
        {
            Model = reference.Model.Id,
            PhotoKind = picture?.HasPhotoFrame == true ? picture.Kind : picture?.Kind == "class" ? "class" : "drawing",
            PhotoUrl = picture?.Kind == "vendor" ? picture.Source : string.Empty,
            PhotoSha256 = picture?.HasPhotoFrame == true ? picture.Key : string.Empty,
            View = picture?.View ?? string.Empty,
            Markers = KnownReferencePositions(),
            Source = T("Layout page"),
            Callouts = sheet
        };
        string file = DeviceReferenceService.CalloutFile(set.Model, calloutPictureKey);
        try
        {
            if (picture?.IsPhoto == true && reference.Photo != null)
            {
                string png = Path.ChangeExtension(file, ".png");
                Directory.CreateDirectory(Path.GetDirectoryName(png)!);
                if (!File.Exists(png))
                    reference.Photo.Save(png);
                set.LocalPhotoFile = png;
            }
        }
        catch (Exception)
        {
            // No picture encoder (tests) or the file is locked: the engine then asks for --picture.
        }
        try
        {
            file = DeviceReferenceService.SaveCallouts(set, calloutPictureKey);
            if (reference.DeviceName.Length > 0)
                DeviceReferenceService.SetCalloutFileOf(reference.DeviceName, file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LayoutEditorStatus = T("The tables could not be saved: {0}", ex.Message);
            return string.Empty;
        }
        if (markSaved)
            LayoutCallouts.MarkSaved();
        return file;
    }

    // A line dragged to a spot of a part without a position, or a spot dragged: the part's marker moves there.
    private void OnCalloutPartPlaced(string part, double x, double y)
    {
        ReferenceMarker? marker = deviceReference?.Markers.FirstOrDefault(m => string.Equals(m.Id, part, StringComparison.OrdinalIgnoreCase));
        if (marker == null || deviceReference == null)
            return;
        deviceReference.MoveMarker(marker, x, y);
        OnPropertyChanged(nameof(WorkspaceHasUnsavedChanges));
        OnPropertyChanged(nameof(CanSaveWorkspace));
    }

    // After undo/redo on the sheet: the parts' markers go where the sheet's spots are.
    private void SyncPositionsFromCallouts()
    {
        CalloutSheet? sheet = LayoutCallouts.Sheet;
        if (sheet == null || deviceReference == null)
            return;
        foreach (CalloutTable table in sheet.Tables.Where(t => t.Parts.Count == 1))
        {
            ReferenceMarker? marker = deviceReference.Markers.FirstOrDefault(m => string.Equals(m.Id, table.Parts[0], StringComparison.OrdinalIgnoreCase));
            if (marker == null)
                continue;
            if (table.Known && (!marker.IsShown || Math.Abs(marker.X - table.SpotX) > 1e-4 || Math.Abs(marker.Y - table.SpotY) > 1e-4))
                deviceReference.MoveMarker(marker, table.SpotX, table.SpotY);
            else if (!table.Known && marker.IsShown && marker.IsPlaced)
            {
                marker.IsShown = false;
                marker.IsPlaced = false;
                deviceReference.HasUnsavedChanges = true;
            }
        }
    }

    // The binds changed: the rows show them, the preview titles are read again.
    private void OnBindsReloadedForCallouts()
    {
        calloutTitlesDevice = string.Empty;
        if (layoutCallouts?.HasSheet == true)
        {
            RebuildCalloutRelationItems();
            ApplyCalloutCombos();
            LayoutCallouts.Refresh();
            if (IsPlanePreview(out _, out _))
                _ = LoadCalloutTitlesAsync();
        }
    }

    // ------------------------------------------------------------------ the texts of the rows

    private string ResolvedCalloutModifier()
    {
        string chosen = LayoutCallouts.Sheet?.Modifier ?? string.Empty;
        if (chosen.Length > 0)
            return chosen;
        if (calloutTitlesModifier.Length > 0)
            return calloutTitlesModifier;
        string device = ResolveLayoutEditorContextDevice();
        return allBinds
            .Where(b => b.HasBind && device.Length > 0 && (SameDevice(b.Device, device) || SameDeviceName(b.Device, device)))
            .SelectMany(b => DisplayText.List(b.ModifiersSummary))
            .Where(m => m.Length > 0 && m is not ("-" or "<none>"))
            .GroupBy(m => m, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Key).FirstOrDefault() ?? string.Empty;
    }

    private bool IsPlanePreview(out string game, out string plane)
    {
        game = plane = string.Empty;
        string value = SelectedCalloutPreview ?? string.Empty;
        int split = value.IndexOf(" · ", StringComparison.Ordinal);
        if (value == RelationNamesPreview || split <= 0)
            return false;
        game = value[..split];
        plane = value[(split + 3)..];
        return true;
    }

    // The modifier combination of a column of the shown sheet: 0 = none, else the sheet's combination (the old single
    // column: the picked or most used modifier).
    private string CalloutColumnCombo(int column) =>
        column <= 0 ? string.Empty : LayoutCallouts.Sheet?.ComboOfColumn(column) ?? ResolvedCalloutModifier();

    // A row: the relation bound there under exactly the column's modifiers (View), or in a preview the game's own menu
    // title for that aircraft.
    private string CalloutRowText(string input, int column)
    {
        bool modifier = column > 0;
        string mod = CalloutColumnCombo(column);
        if (modifier && mod.Length == 0)
            return string.Empty;
        if (IsPlanePreview(out string game, out string plane))
        {
            string token = modifier ? mod + "+" + input : input;
            return calloutTitles.TryGetValue(game + "|" + plane, out Dictionary<string, string>? titles) && titles.TryGetValue(token, out string? title) ? title : string.Empty;
        }
        string device = ResolveLayoutEditorContextDevice();
        return device.Length == 0 ? string.Empty : FindRelationBoundTo(device, input, mod);
    }

    // The games and aircraft the device's binds are active in, with their menu titles (engine layout titles).
    private async Task LoadCalloutTitlesAsync()
    {
        string device = ResolveLayoutEditorContextDevice();
        if (device.Length == 0 || string.Equals(device, calloutTitlesDevice, StringComparison.OrdinalIgnoreCase) || EnginePath is not { Length: > 0 })
            return;
        calloutTitlesDevice = device;
        EngineCommandResult? result = await RunEngineAsync(["layout", "titles", "--device", device], includeProfile: true, mutatesProfile: false,
            busyLabel: "Reading the menu titles of " + DeviceDisplayName(device) + "...", forceText: false);
        calloutTitles.Clear();
        calloutTitlesModifier = string.Empty;
        if (result?.ExitCode == 0)
            ParseCalloutTitles(result.StandardOutput);
        else
            calloutTitlesDevice = string.Empty; // Busy or failed: asked again next time.
        string keep = SelectedCalloutPreview;
        CalloutPreviewOptions.Clear();
        CalloutPreviewOptions.Add(RelationNamesPreview);
        foreach (string key in calloutTitles.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
            CalloutPreviewOptions.Add(key.Replace("|", " · "));
        SelectedCalloutPreview = CalloutPreviewOptions.Contains(keep) ? keep : RelationNamesPreview;
        OnPropertyChanged(nameof(CalloutModifierInUse));
        LayoutCallouts.Refresh();
    }

    // The JSON of "engine layout titles": { Device, Modifier, Targets: [ { Game, Plane, Titles: { token: title } } ] }.
    public void ParseCalloutTitles(string? output)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(ExtractJsonPayload(output ?? string.Empty));
            calloutTitlesModifier = ReadJsonString(doc.RootElement, "Modifier");
            if (!doc.RootElement.TryGetProperty("Targets", out JsonElement targets) || targets.ValueKind != JsonValueKind.Array)
                return;
            foreach (JsonElement target in targets.EnumerateArray())
            {
                string game = ReadJsonString(target, "Game"), plane = ReadJsonString(target, "Plane");
                if (game.Length == 0 || plane.Length == 0 || !target.TryGetProperty("Titles", out JsonElement titles) || titles.ValueKind != JsonValueKind.Object)
                    continue;
                Dictionary<string, string> map = new(StringComparer.OrdinalIgnoreCase);
                foreach (JsonProperty title in titles.EnumerateObject())
                    map[title.Name] = title.Value.GetString() ?? string.Empty;
                calloutTitles[game + "|" + plane] = map;
            }
        }
        catch (JsonException)
        {
        }
    }

    private void RebuildCalloutModifierOptions()
    {
        CalloutModifierOptions.Clear();
        CalloutModifierOptions.Add(AutoModifierChoice);
        CalloutModifierOptions.Add(AllCombinationsChoice);
        foreach (string combo in CalloutCombos.AllOf(ModifierNames, 15))
            CalloutModifierOptions.Add(CalloutCombos.Label(combo));
        OnPropertyChanged(nameof(CalloutModifier));
    }

    // ------------------------------------------------------------------ binding on the rows

    // The relations the side panel offers for dragging onto a row.
    public ObservableCollection<string> CalloutRelationItems { get; } = [];

    [ObservableProperty]
    private string calloutRelationSearch = string.Empty;

    partial void OnCalloutRelationSearchChanged(string value) => RebuildCalloutRelationItems();

    private void RebuildCalloutRelationItems()
    {
        string filter = (CalloutRelationSearch ?? string.Empty).Trim();
        List<string> names = allRelations.Select(r => r.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n) && (filter.Length == 0 || n.Contains(filter, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Take(500)
            .ToList();
        if (names.SequenceEqual(CalloutRelationItems))
            return;
        CalloutRelationItems.Clear();
        foreach (string name in names)
            CalloutRelationItems.Add(name);
    }

    // A relation dropped on a row (or a row cleared): bound to that input of the device, in the modifier column with
    // the sheet's modifier.
    private async Task BindCalloutRowAsync(CalloutBindRequest request)
    {
        string device = ResolveLayoutEditorContextDevice();
        if (device.Length == 0)
        {
            LayoutEditorStatus = T("This picture is not one of your devices: pick the device on the left to bind.");
            return;
        }
        string modifier = CalloutColumnCombo(request.ColumnIndex);
        if (request.ColumnIndex > 0 && modifier.Length == 0)
        {
            LayoutEditorStatus = T("Pick the modifier of the second column first (Tables, on the right).");
            return;
        }
        string current = CurrentRelationOf(request.Input, modifier);
        string label = request.Label + (modifier.Length > 0 ? " (" + CalloutCombos.Label(modifier) + ")" : string.Empty);
        string next = request.Relation ?? NoRelation;
        if (request.Relation == null && current.Length == 0)
        {
            LayoutEditorStatus = T("{0} is free already.", label);
            return;
        }
        bool ok = await AssignInputAsync(device, request.Input, modifier, label, current.Length > 0 ? current : NoRelation, next);
        LayoutEditorStatus = ok
            ? (request.Relation == null ? T("{0} is free now.", label) : T("{0} is bound to {1}.", request.Relation, label))
            : T("The change failed; see Engine Output.");
        LayoutCallouts.Refresh();
    }

    // A row clicked on the sheet: its input is the selected one (the side panel, Bind).
    public void SelectCalloutInput(string input)
    {
        if (deviceReference == null)
            return;
        // A row without an input: a hat's press that is not mapped yet.
        if (string.IsNullOrWhiteSpace(input))
        {
            if (LayoutCallouts.SelectedTable is { } table && LayoutCallouts.RowOf(table, LayoutCallouts.SelectedRow) is { Input.Length: 0 } empty)
                OpenPressMapping(empty.Part);
            return;
        }
        if (HasPendingPress)
            CancelPendingPress();
        ReferenceLegendRow? row = deviceReference.Legend.FirstOrDefault(r => string.Equals(r.Input, input, StringComparison.OrdinalIgnoreCase));
        if (row != null)
            deviceReference.SelectedLegendRow = row;
    }

    // ------------------------------------------------------------------ the edit tools

    [RelayCommand]
    private void CalloutAutoArrange() => LayoutCallouts.AutoArrange();

    [RelayCommand]
    private void CalloutResetLayout() => LayoutCallouts.ResetLayout();

    [RelayCommand]
    private void CalloutToggleModifierColumn()
    {
        if (LayoutCallouts.SelectedTable is { } table)
            LayoutCallouts.SetModifierColumn(table, !table.ShowModifier);
    }

    [RelayCommand]
    private void CalloutAddRow()
    {
        if (LayoutCallouts.SelectedTable is { } table)
            LayoutCallouts.ChangeRows(table, 1);
    }

    [RelayCommand]
    private void CalloutRemoveRow()
    {
        if (LayoutCallouts.SelectedTable is { } table)
            LayoutCallouts.ChangeRows(table, -1);
    }

    [RelayCommand]
    private void CalloutRerouteLine()
    {
        if (LayoutCallouts.SelectedTable is { } table)
            LayoutCallouts.RerouteLine(table);
    }

    [RelayCommand]
    private void CalloutDeleteLine()
    {
        CalloutSheet? sheet = LayoutCallouts.Sheet;
        CalloutLine? line = LayoutCallouts.SelectedLine ?? (LayoutCallouts.SelectedTable is { } t ? sheet?.LineOf(t.Id) : null);
        if (line != null)
            LayoutCallouts.DeleteLine(line);
    }

    [RelayCommand]
    private void CalloutSplitGroup()
    {
        if (LayoutCallouts.SelectedTable is { } table)
            LayoutCallouts.SplitGroup(table);
    }

    [RelayCommand]
    private void CalloutRegroup() => LayoutCallouts.Regroup();

    [RelayCommand]
    private void CalloutTogglePin()
    {
        if (LayoutCallouts.SelectedTable is { } table)
            LayoutCallouts.SetPinned(table, !table.Pinned);
    }

    // The cheat sheet of the shown device: its sheet's file (saved first).
    private bool IsGeneratedLayoutPath(string path)
    {
        string p = (path ?? string.Empty).Replace('/', '\\');
        return p.Contains("\\AutoLayouts\\", StringComparison.OrdinalIgnoreCase) || p.Contains("\\LayoutTemplates\\", StringComparison.OrdinalIgnoreCase);
    }
}
