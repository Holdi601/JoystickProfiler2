using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JoyPro.DeviceReference;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;

namespace JoyPro.Gui.ViewModels;

// The picture of the Layout workspace (and of "Bind on the picture" in Bind Studio): either the device reference of the
// picked device (docs/device-reference.md: your photo > the maker's photo > JoyPro's drawing, a marker on every part,
// legend with physical names and bound commands, marker assistant, grip choice), or the device's layout file (the
// user's own .layout with label markers, tables and lines, the source of the cheat sheets). One canvas shows both: the
// Workspace* properties are what it binds.
public sealed partial class MainWindowViewModel
{
    public ObservableCollection<WorkspacePictureChoice> WorkspacePictures { get; } = [];

    // The legend rows of the reference, filtered by LayoutLegendFilter.
    public ObservableCollection<ReferenceLegendRow> ReferenceLegendItems { get; } = [];

    // The inputs of the selected part (a hat: its directions and its press).
    public ObservableCollection<ReferenceLegendRow> SelectedPartRows { get; } = [];

    [ObservableProperty]
    private bool layoutShowsLayoutFile;

    [ObservableProperty]
    private WorkspacePictureChoice? selectedWorkspacePicture;

    private bool syncingWorkspacePicture;
    private DeviceReferenceViewModel? observedReference;

    public bool LayoutShowsReference => !LayoutShowsLayoutFile;

    public bool HasReferenceModel => deviceReference?.HasModel == true;

    public IImage? WorkspaceImage => LayoutShowsLayoutFile ? LayoutEditorPreviewImage : deviceReference?.CanvasPhoto;

    public DeviceSchematic? WorkspaceSchematic => LayoutShowsLayoutFile ? null : deviceReference?.CanvasSchematic;

    // JoyPro's drawing is the shown picture: the Lines | Cel-shaded choice is offered next to it.
    public bool WorkspaceShowsLineArt => !LayoutShowsLayoutFile && deviceReference?.CanvasPhoto is Views.Controls.LineArtImage;

    public double WorkspaceWidth => LayoutShowsLayoutFile ? LayoutEditorCanvasWidth : deviceReference?.PictureWidth ?? 0;

    public double WorkspaceHeight => LayoutShowsLayoutFile ? LayoutEditorCanvasHeight : deviceReference?.PictureHeight ?? 0;

    public IEnumerable WorkspaceMarkers => LayoutShowsLayoutFile ? LayoutEditorMarkers : (IEnumerable?)deviceReference?.CanvasMarkers ?? Array.Empty<IDeviceMarker>();

    // ---- JoyPro 1's look on the user's own layout: the bound command as plain text in the layout's font, size and
    //      color at the position the user gave it (the boxes and lines are part of his picture) ----

    private string layoutTextFont = string.Empty;
    private double layoutTextSize;
    private IBrush layoutTextBrush = Brushes.Black;
    private bool layoutHasTextStyle;

    public bool WorkspacePlainLabels => LayoutShowsLayoutFile && layoutHasTextStyle;

    public FontFamily WorkspaceLabelFont => layoutTextFont.Length > 0 ? new FontFamily(layoutTextFont + ", Segoe UI, " + JoyPro.Gui.Localization.Languages.FallbackFonts(JoyPro.Gui.Localization.Loc.Current)) : FontFamily.Default;

    public double WorkspaceLabelSize => layoutTextSize > 0 ? layoutTextSize : 14;

    public IBrush WorkspaceLabelBrush => layoutTextBrush;

    private void ApplyLayoutTextStyle(string fontName, int fontSize, string color)
    {
        string source = (LayoutEditorSourcePath ?? string.Empty).Replace('/', '\\');
        // JoyPro 2's own generated layouts (AutoLayouts) carry a default style and no boxes for the text.
        layoutHasTextStyle = !string.IsNullOrWhiteSpace(fontName) && fontSize > 0 && !source.Contains("\\AutoLayouts\\", StringComparison.OrdinalIgnoreCase);
        layoutTextFont = (fontName ?? string.Empty).Trim();
        layoutTextSize = fontSize;
        layoutTextBrush = Color.TryParse(color, out Color parsed) ? new SolidColorBrush(parsed) : Brushes.Black;
        OnPropertyChanged(nameof(WorkspacePlainLabels));
        OnPropertyChanged(nameof(WorkspaceLabelFont));
        OnPropertyChanged(nameof(WorkspaceLabelSize));
        OnPropertyChanged(nameof(WorkspaceLabelBrush));
    }

    // The canvas' selection: a layout marker, or a reference part (ReferenceCanvasMarker).
    public object? WorkspaceSelectedMarker
    {
        get => LayoutShowsLayoutFile ? SelectedLayoutEditorMarker : deviceReference?.SelectedCanvasMarker;
        set
        {
            if (LayoutShowsLayoutFile)
                SelectedLayoutEditorMarker = value as LayoutEditorMarkerItem;
            else if (deviceReference != null)
                deviceReference.SelectedCanvasMarker = value as ReferenceCanvasMarker;
            OnPropertyChanged();
        }
    }

    // ---- What the side panel shows about the selected input ----

    public ReferenceLegendRow? SelectedReferenceRow => LayoutShowsReference ? deviceReference?.SelectedLegendRow : null;

    public bool HasWorkspaceInput => LayoutShowsLayoutFile ? SelectedLayoutEditorMarker != null : SelectedReferenceRow != null;

    public bool HasNoWorkspaceInput => !HasWorkspaceInput;

    // "BTN 5", "TMS" (the part's badge).
    public string WorkspaceInputTitle => LayoutShowsLayoutFile
        ? SelectedLayoutEditorMarker?.ShortLabel ?? string.Empty
        : SelectedReferenceRow == null ? string.Empty : SelectedReferenceRow.HasInput ? DeviceMarkerText.ShortLabel(SelectedReferenceRow.Input) : SelectedReferenceRow.Badge;

    // The physical name ("TMS hat up") of a reference input, the DirectInput name of a layout marker.
    public string WorkspaceInputName => LayoutShowsLayoutFile ? SelectedLayoutEditorMarker?.Token ?? string.Empty : SelectedReferenceRow?.Name ?? string.Empty;

    public string WorkspaceInputToken => LayoutShowsLayoutFile ? SelectedLayoutEditorMarker?.Token ?? string.Empty : SelectedReferenceRow?.InputText ?? string.Empty;

    public string WorkspaceInputKind => LayoutShowsLayoutFile
        ? SelectedLayoutEditorMarker?.KindLabel ?? string.Empty
        : SelectedReferenceRow == null ? string.Empty : KindLabelOfInput(SelectedReferenceRow.Input);

    public string WorkspaceInputModifier => LayoutShowsLayoutFile ? SelectedLayoutEditorMarker?.Modifier ?? string.Empty : string.Empty;

    public bool WorkspaceInputHasModifier => WorkspaceInputModifier.Length > 0;

    // What it does: the bound relation (layout) or the legend's command (bound relation or the user's own text).
    public string WorkspaceInputCommand => LayoutShowsLayoutFile ? SelectedLayoutEditorMarker?.Command ?? string.Empty : SelectedReferenceRow?.Command ?? string.Empty;

    public bool WorkspaceInputIsBound => WorkspaceInputCommand.Length > 0;

    public bool HasSelectedPartRows => LayoutShowsReference && SelectedPartRows.Count > 1;

    // ---- Picture, status, saving ----

    public string LayoutWorkspacePictureCaption => LayoutShowsLayoutFile
        ? LayoutWorkspaceTemplateName
        : deviceReference?.Title is { Length: > 0 } title ? title + " · " + (deviceReference.SelectedPicture?.Title ?? "JoyPro drawing") : string.Empty;

    // The reference's message (a first guess on a photo, the assistant, saved markers), else what it is ("Thrustmaster ·
    // Stick · USB 044F:0402 · all 25 inputs known"); the layout file's messages for the layout file.
    public string WorkspaceStatus => LayoutShowsReference && deviceReference != null
        ? deviceReference.Status is { Length: > 0 } status ? status : deviceReference.Subtitle
        : LayoutEditorStatus;

    public bool WorkspaceHasUnsavedChanges => LayoutShowsLayoutFile
        ? LayoutEditorHasUnsavedChanges
        : deviceReference?.HasUnsavedChanges == true || layoutCallouts?.HasUnsavedChanges == true;

    public bool CanSaveWorkspace => LayoutShowsLayoutFile
        ? CanSaveLayoutEditorTemplate
        : deviceReference?.HasModel == true && (deviceReference.HasUnsavedChanges || layoutCallouts?.HasUnsavedChanges == true);

    public string SaveWorkspaceTip => LayoutShowsLayoutFile ? T("Save the markers into the layout file (Ctrl+S)")
        : LayoutShowsCallouts ? T("Keep the tables, lines and marker positions of this picture (Ctrl+S)")
        : T("Keep the marker positions of this picture (Ctrl+S)");

    public bool HasReferenceGripChoices => LayoutShowsReference && deviceReference?.HasGripChoices == true;

    public bool HasReferenceAssistant => LayoutShowsReference && deviceReference?.HasAssistant == true;

    // The device label on the picture (hidden while the marker assistant's panel takes its place).
    public bool ShowWorkspaceLabel => HasLayoutWorkspacePicture && !HasReferenceAssistant;

    // "Which one is it?" over the picture (a base whose grip is not known, a shared USB id, an unknown device).
    public bool ShowReferenceQuestion => deviceReference?.HasQuestion == true && !HasReferenceAssistant;

    // The shown reference picture knows no part's position: the marker assistant is offered over it.
    public bool ShowPlaceMarkersHint => LayoutShowsReference && deviceReference?.HasNoKnownPositions == true && !ShowReferenceQuestion;

    // A layout file is the user's own (his layout, a JoyPro 1 layout, a picture he chose) unless the engine picked a
    // shipped template, its drawing or a placeholder: those are shown as the reference picture instead.
    public static bool IsOwnLayout(LayoutSuggestionItem item) => item.IsOwnLayout;

    // Devices whose layout file the user saved in this session (his own layout from now on).
    private readonly HashSet<string> savedLayoutDevices = new(StringComparer.OrdinalIgnoreCase);

    private bool PrefersLayoutFile(LayoutSuggestionItem item) => item.IsOwnLayout || savedLayoutDevices.Contains((item.Device ?? string.Empty).Trim());

    private static string KindLabelOfInput(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return T("Part");
        return DeviceVocabulary.InputKind(input) switch
        {
            "axis" => "Axis",
            "hat" => "Hat",
            _ => T("Button")
        };
    }

    // Called once the DeviceReference exists (MainWindowViewModel.DeviceReference.cs).
    private void ObserveDeviceReference(DeviceReferenceViewModel reference)
    {
        if (ReferenceEquals(observedReference, reference))
            return;
        observedReference = reference;
        reference.PropertyChanged += OnDeviceReferencePropertyChanged;
        // The legend is rebuilt row by row: the filtered copy follows once it is complete (PictureChanged).
        reference.Pictures.CollectionChanged += (_, _) => RebuildWorkspacePictures();
        reference.PictureChanged += (_, _) =>
        {
            RebuildReferenceLegend();
            OnPropertyChanged(nameof(LayoutLegendSummary));
            RefreshLayoutCallouts();
            RefreshWorkspaceDeviceIcon();
            IsDeviceIconPickerOpen = false;
        };
        // The grip picked for a base is kept: the Layout page shows it next time, the device cards show it.
        reference.GripChosen += (_, grip) => OnReferenceGripChosen(reference.DeviceName, grip);
        reference.DeviceChosen += (_, choice) => OnReferenceDeviceChosen(reference.DeviceName, choice);
    }

    // The user said what a device is ("Which one is it?"): kept per device like a grip.
    private void OnReferenceDeviceChosen(string device, string choice)
    {
        if (string.IsNullOrWhiteSpace(device) || string.IsNullOrWhiteSpace(choice))
            return;
        try
        {
            DeviceReferenceService.SetChoice(device, choice);
        }
        catch (Exception)
        {
            return;
        }
        if (lastHudDevices.Count > 0)
            _ = RebuildDeviceCardsAsync();
    }

    private void OnDeviceReferencePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(DeviceReferenceViewModel.CanvasPhoto):
            case nameof(DeviceReferenceViewModel.CanvasSchematic):
            case nameof(DeviceReferenceViewModel.PictureWidth):
            case nameof(DeviceReferenceViewModel.PictureHeight):
                NotifyWorkspacePicture();
                break;
            case nameof(DeviceReferenceViewModel.SelectedCanvasMarker):
                OnPropertyChanged(nameof(WorkspaceSelectedMarker));
                break;
            case nameof(DeviceReferenceViewModel.SelectedLegendRow):
            case nameof(DeviceReferenceViewModel.SelectedMarker):
                if (layoutCallouts?.HasSheet == true)
                    LayoutCallouts.SelectInput(deviceReference?.SelectedLegendRow?.Input);
                RebuildSelectedPartRows();
                NotifyWorkspaceInput();
                LayoutBindSearch = string.Empty;
                RebuildLayoutBindCandidates();
                break;
            case nameof(DeviceReferenceViewModel.SelectedPicture):
                SyncSelectedWorkspacePicture();
                OnPropertyChanged(nameof(LayoutWorkspacePictureCaption));
                break;
            case nameof(DeviceReferenceViewModel.Status):
            case nameof(DeviceReferenceViewModel.Subtitle):
                OnPropertyChanged(nameof(WorkspaceStatus));
                break;
            case nameof(DeviceReferenceViewModel.HasUnsavedChanges):
                OnPropertyChanged(nameof(WorkspaceHasUnsavedChanges));
                OnPropertyChanged(nameof(CanSaveWorkspace));
                break;
            case nameof(DeviceReferenceViewModel.Title):
            case nameof(DeviceReferenceViewModel.Model):
            case nameof(DeviceReferenceViewModel.HasModel):
                NotifyWorkspacePicture();
                break;
            case nameof(DeviceReferenceViewModel.HasGripChoices):
            case nameof(DeviceReferenceViewModel.SelectedGrip):
                OnPropertyChanged(nameof(HasReferenceGripChoices));
                break;
            case nameof(DeviceReferenceViewModel.Assistant):
            case nameof(DeviceReferenceViewModel.HasAssistant):
                OnPropertyChanged(nameof(HasReferenceAssistant));
                OnPropertyChanged(nameof(ShowWorkspaceLabel));
                OnPropertyChanged(nameof(ShowPlaceMarkersHint));
                OnPropertyChanged(nameof(ShowReferenceQuestion));
                NotifyCallouts();
                break;
            case nameof(DeviceReferenceViewModel.KnownMarkerCount):
            case nameof(DeviceReferenceViewModel.HasNoKnownPositions):
            case nameof(DeviceReferenceViewModel.Question):
                OnPropertyChanged(nameof(ShowPlaceMarkersHint));
                OnPropertyChanged(nameof(ShowReferenceQuestion));
                NotifyCallouts();
                break;
        }
    }

    private void NotifyWorkspacePicture()
    {
        OnPropertyChanged(nameof(WorkspaceImage));
        OnPropertyChanged(nameof(WorkspaceSchematic));
        OnPropertyChanged(nameof(WorkspaceShowsLineArt));
        OnPropertyChanged(nameof(WorkspaceWidth));
        OnPropertyChanged(nameof(WorkspaceHeight));
        OnPropertyChanged(nameof(WorkspaceMarkers));
        OnPropertyChanged(nameof(WorkspaceSelectedMarker));
        OnPropertyChanged(nameof(WorkspacePlainLabels));
        OnPropertyChanged(nameof(ShowPlaceMarkersHint));
        OnPropertyChanged(nameof(ShowReferenceQuestion));
        OnPropertyChanged(nameof(HasReferenceModel));
        OnPropertyChanged(nameof(HasLayoutWorkspacePicture));
        OnPropertyChanged(nameof(HasNoLayoutWorkspacePicture));
        OnPropertyChanged(nameof(LayoutWorkspacePictureCaption));
        OnPropertyChanged(nameof(LayoutWorkspaceDeviceTitle));
        OnPropertyChanged(nameof(WorkspaceStatus));
        OnPropertyChanged(nameof(WorkspaceHasUnsavedChanges));
        OnPropertyChanged(nameof(CanSaveWorkspace));
        OnPropertyChanged(nameof(SaveWorkspaceTip));
        OnPropertyChanged(nameof(HasReferenceGripChoices));
        OnPropertyChanged(nameof(HasReferenceAssistant));
        OnPropertyChanged(nameof(ShowWorkspaceLabel));
        OnPropertyChanged(nameof(LayoutLegendSummary));
    }

    private void NotifyWorkspaceInput()
    {
        OnPropertyChanged(nameof(SelectedReferenceRow));
        OnPropertyChanged(nameof(HasWorkspaceInput));
        OnPropertyChanged(nameof(HasNoWorkspaceInput));
        OnPropertyChanged(nameof(WorkspaceInputTitle));
        OnPropertyChanged(nameof(WorkspaceInputName));
        OnPropertyChanged(nameof(WorkspaceInputToken));
        OnPropertyChanged(nameof(WorkspaceInputKind));
        OnPropertyChanged(nameof(WorkspaceInputModifier));
        OnPropertyChanged(nameof(WorkspaceInputHasModifier));
        OnPropertyChanged(nameof(WorkspaceInputCommand));
        OnPropertyChanged(nameof(WorkspaceInputIsBound));
        OnPropertyChanged(nameof(HasSelectedPartRows));
    }

    partial void OnLayoutShowsLayoutFileChanged(bool value)
    {
        OnPropertyChanged(nameof(LayoutShowsReference));
        NotifyCallouts();
        ClearLayoutUndo();
        NotifyWorkspacePicture();
        NotifyWorkspaceInput();
        SyncSelectedWorkspacePicture();
        RebuildLayoutBindCandidates();
        if (value)
            return;
        // The layout file's tools do nothing on the reference picture.
        LayoutPlaceMarkerMode = false;
        LayoutPlaceTableMode = false;
        LayoutPlaceConnectorSourceMode = false;
        LayoutPlaceConnectorPointMode = false;
    }

    // The picture choice: the reference's pictures (your photo, the maker's photo, JoyPro's drawing) and the layout file.
    private void RebuildWorkspacePictures()
    {
        syncingWorkspacePicture = true;
        try
        {
            WorkspacePictures.Clear();
            if (deviceReference != null)
            {
                // The picture strip in the reference's order: your photo, JoyPro's line drawing with its positions, the
                // maker's photo, JoyPro's schematic drawing, line drawings without positions, the class outline.
                foreach (ReferencePictureOption option in deviceReference.Pictures)
                    WorkspacePictures.Add(new WorkspacePictureChoice { Title = option.Title, Reference = option });
            }
            // The layout file is a picture of its own when it is the user's (his JoyPro 1 layout, his picture, saved in
            // this session); a layout the engine generated (its drawing, a placeholder) only feeds the cheat sheets.
            bool ownLayout = layoutHasTextStyle || (SelectedLayoutSuggestion != null && PrefersLayoutFile(SelectedLayoutSuggestion)) ||
                             savedLayoutDevices.Contains(layoutEditorContextDevice ?? string.Empty);
            if (ownLayout && !string.IsNullOrWhiteSpace(LayoutEditorSourcePath) && (LayoutEditorPreviewImage != null || LayoutEditorMarkers.Count > 0))
                WorkspacePictures.Insert(0, new WorkspacePictureChoice { Title = layoutHasTextStyle ? T("Your layout") : T("Layout file: {0}", LayoutWorkspaceTemplateName), IsLayoutFile = true });
        }
        finally
        {
            syncingWorkspacePicture = false;
        }
        OnPropertyChanged(nameof(HasSeveralWorkspacePictures));
        SyncSelectedWorkspacePicture();
    }

    private void SyncSelectedWorkspacePicture()
    {
        syncingWorkspacePicture = true;
        try
        {
            SelectedWorkspacePicture = LayoutShowsLayoutFile
                ? WorkspacePictures.FirstOrDefault(p => p.IsLayoutFile)
                : WorkspacePictures.FirstOrDefault(p => p.Reference != null && ReferenceEquals(p.Reference, deviceReference?.SelectedPicture))
                  ?? WorkspacePictures.FirstOrDefault(p => p.Reference != null && p.Reference.Kind == deviceReference?.PictureKind);
        }
        finally
        {
            syncingWorkspacePicture = false;
        }
        foreach (WorkspacePictureChoice choice in WorkspacePictures)
            choice.IsSelected = ReferenceEquals(choice, SelectedWorkspacePicture);
    }

    // A view of the picture strip clicked.
    [RelayCommand]
    private void SelectWorkspacePicture(WorkspacePictureChoice? choice)
    {
        if (choice != null)
            SelectedWorkspacePicture = choice;
        foreach (WorkspacePictureChoice item in WorkspacePictures)
            item.IsSelected = ReferenceEquals(item, SelectedWorkspacePicture);
    }

    public bool HasSeveralWorkspacePictures => WorkspacePictures.Count > 1;

    partial void OnSelectedWorkspacePictureChanged(WorkspacePictureChoice? value)
    {
        if (syncingWorkspacePicture || value == null)
            return;
        if (value.IsLayoutFile)
        {
            LayoutShowsLayoutFile = true;
            return;
        }
        LayoutShowsLayoutFile = false;
        if (deviceReference != null && value.Reference != null && !ReferenceEquals(deviceReference.SelectedPicture, value.Reference))
            deviceReference.SelectedPicture = value.Reference;
    }

    private void RebuildReferenceLegend()
    {
        string filter = (LayoutLegendFilter ?? string.Empty).Trim();
        List<ReferenceLegendRow> rows = deviceReference == null
            ? []
            : deviceReference.Legend.Where(r => filter.Length == 0 ||
                                                r.Input.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                                                r.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                                                r.Command.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        if (!rows.SequenceEqual(ReferenceLegendItems))
        {
            // A list that loses its selected row clears the selection: keep it.
            ReferenceLegendRow? selected = deviceReference?.SelectedLegendRow;
            ReferenceLegendItems.Clear();
            foreach (ReferenceLegendRow row in rows)
                ReferenceLegendItems.Add(row);
            if (deviceReference != null && selected != null && deviceReference.Legend.Contains(selected) && deviceReference.SelectedLegendRow != selected)
                deviceReference.SelectedLegendRow = selected;
        }
        RebuildSelectedPartRows();
        OnPropertyChanged(nameof(LayoutLegendSummary));
    }

    private void RebuildSelectedPartRows()
    {
        ReferenceMarker? part = deviceReference?.SelectedMarker;
        List<ReferenceLegendRow> rows = part == null || deviceReference == null ? [] : deviceReference.Legend.Where(r => ReferenceEquals(r.Marker, part)).ToList();
        if (!rows.SequenceEqual(SelectedPartRows))
        {
            ReferenceLegendRow? selected = deviceReference?.SelectedLegendRow;
            SelectedPartRows.Clear();
            foreach (ReferenceLegendRow row in rows)
                SelectedPartRows.Add(row);
            if (deviceReference != null && selected != null && deviceReference.Legend.Contains(selected) && deviceReference.SelectedLegendRow != selected)
                deviceReference.SelectedLegendRow = selected;
        }
        OnPropertyChanged(nameof(HasSelectedPartRows));
    }

    // "12 of 25 inputs bound" of the reference's legend.
    private string ReferenceLegendSummary()
    {
        if (deviceReference == null || deviceReference.Legend.Count == 0)
            return T("No inputs yet");
        List<ReferenceLegendRow> inputs = deviceReference.Legend.Where(r => r.HasInput).ToList();
        int bound = inputs.Count(r => r.Command.Length > 0);
        return T("{0} of {1} inputs bound", bound.ToString(CultureInfo.InvariantCulture), inputs.Count.ToString(CultureInfo.InvariantCulture));
    }

    // ---- Picking a device anywhere (Bind Studio, auto pick) ----

    // Shows a device in the workspace: its layout suggestion when the list has it (layout file and reference), else the
    // reference alone.
    public async Task ShowWorkspaceDeviceAsync(string device)
    {
        string wanted = (device ?? string.Empty).Trim();
        if (wanted.Length == 0)
            return;
        LayoutSuggestionItem? suggestion = LayoutSuggestions.FirstOrDefault(s => SameDevice(s.Device, wanted));
        if (suggestion == null && !TryResolveRememberedLayoutPathForDevice(wanted, out _))
        {
            // Not listed yet (a device of the profile's binds): ask for its layout; a layout file found joins the list.
            EngineCommandResult? result = await RunEngineAsync(["layout", "suggest", "--device", wanted, "--apply", "false"], includeProfile: false, mutatesProfile: false,
                busyLabel: N("Finding the device's layout..."), forceText: false);
            if (result?.ExitCode == 0 && TryParseLayoutSuggestions(result.StandardOutput, out List<LayoutSuggestionItem> found) &&
                found.FirstOrDefault(s => SameDevice(s.Device, wanted)) is { } item && item.HasLayout && File.Exists(item.LayoutPath))
            {
                LayoutSuggestions.Add(item);
                suggestion = item;
            }
        }
        if (suggestion != null)
        {
            if (!ReferenceEquals(SelectedLayoutSuggestion, suggestion))
                SelectedLayoutSuggestion = suggestion;
            else
                await ShowDeviceReferenceForAsync(wanted);
            return;
        }
        bool otherDevice = !SameDevice(layoutEditorContextDevice, wanted);
        layoutEditorContextDevice = wanted;
        ClearWorkspaceLiveInput();
        // Not in the device list: none of the listed devices is the shown one.
        SelectedLayoutSuggestion = null;
        if (otherDevice)
        {
            LayoutEditorMarkers.Clear();
            LayoutEditorTables.Clear();
            LayoutEditorConnectors.Clear();
            Avalonia.Media.Imaging.Bitmap? previous = LayoutEditorPreviewImage;
            LayoutEditorPreviewImage = null;
            previous?.Dispose();
            LayoutEditorSourcePath = string.Empty;
            LayoutEditorOutputPath = string.Empty;
            if (TryResolveRememberedLayoutPathForDevice(wanted, out string path) && File.Exists(path))
            {
                LayoutEditorSourcePath = path;
                await LoadLayoutEditorSourceAsync();
            }
            LayoutShowsLayoutFile = false;
            ClearLayoutUndo();
        }
        await ShowDeviceReferenceForAsync(wanted);
        RebuildWorkspacePictures();
        OnPropertyChanged(nameof(LayoutWorkspaceDeviceTitle));
    }

    // No device picked: the empty picture area (and the device reference shows nothing).
    public void ClearLayoutWorkspace()
    {
        layoutEditorContextDevice = string.Empty;
        SelectedLayoutSuggestion = null;
        LayoutEditorMarkers.Clear();
        LayoutEditorTables.Clear();
        LayoutEditorConnectors.Clear();
        Avalonia.Media.Imaging.Bitmap? previous = LayoutEditorPreviewImage;
        LayoutEditorPreviewImage = null;
        previous?.Dispose();
        LayoutEditorSourcePath = string.Empty;
        LayoutEditorOutputPath = string.Empty;
        deviceReference?.Clear();
        LayoutShowsLayoutFile = false;
        ClearLayoutUndo();
        RebuildWorkspacePictures();
        RebuildReferenceLegend();
        NotifyWorkspacePicture();
        NotifyWorkspaceInput();
    }

    // The Layout page opened without a picked device: the first one (a known device first) is shown at once.
    private void PickFirstLayoutDevice()
    {
        if (SelectedLayoutSuggestion != null || LayoutSuggestions.Count == 0)
            return;
        SelectedLayoutSuggestion = LayoutSuggestions.FirstOrDefault(s => s.HasLayout) ?? LayoutSuggestions[0];
    }

    // ---- Edit tools of the reference ----

    [RelayCommand]
    private void SaveWorkspace()
    {
        if (LayoutShowsLayoutFile)
        {
            _ = SaveLayoutEditorTemplateCommand.ExecuteAsync(null);
            return;
        }
        if (deviceReference?.HasUnsavedChanges == true || !LayoutShowsCallouts)
            deviceReference?.SaveMarkersCommand.Execute(null);
        if (LayoutCallouts.HasSheet && SaveLayoutCalloutsFile(markSaved: true).Length > 0 && LayoutShowsCallouts)
            LayoutEditorStatus = T("Tables, lines and markers of this picture saved; the cheat sheets use them.");
        OnPropertyChanged(nameof(WorkspaceHasUnsavedChanges));
        OnPropertyChanged(nameof(CanSaveWorkspace));
        layoutUndo.Clear();
        layoutRedo.Clear();
        NotifyLayoutUndoState();
    }

    // Autosave (docs/profile-history.md): the marker positions and callout tables of the shown picture, like Save but
    // the undo of the layout edits stays. The old layout-file editor keeps its Save (a save writes and reloads a template).
    private void AutosaveReferenceWorkspace()
    {
        if (LayoutShowsLayoutFile || !CanSaveWorkspace)
            return;
        if (deviceReference?.HasUnsavedChanges == true)
            deviceReference.SaveMarkersCommand.Execute(null);
        if (LayoutCallouts.HasSheet && LayoutCallouts.HasUnsavedChanges)
            SaveLayoutCalloutsFile(markSaved: true);
        OnPropertyChanged(nameof(WorkspaceHasUnsavedChanges));
        OnPropertyChanged(nameof(CanSaveWorkspace));
    }

    // A picture file dropped on the canvas: the user's photo of the shown device (the reference shows it at once).
    [RelayCommand]
    private async Task DropWorkspacePhotoAsync(string? file)
    {
        if (deviceReference?.Model == null || string.IsNullOrWhiteSpace(file))
            return;
        LayoutShowsLayoutFile = false;
        await deviceReference.SetUserPhotoAsync(file);
        RebuildWorkspacePictures();
    }

    [RelayCommand]
    private void ResetReferenceMarkers()
    {
        if (deviceReference == null)
            return;
        PushLayoutUndo("reset markers");
        deviceReference.ResetMarkersCommand.Execute(null);
    }

    [RelayCommand]
    private void OpenReferenceAssistant()
    {
        if (deviceReference == null)
            return;
        LayoutShowsLayoutFile = false;
        LayoutCanvasMode = DeviceCanvasMode.EditMarkers;
        deviceReference.OpenAssistantCommand.Execute(null);
        OnPropertyChanged(nameof(HasReferenceAssistant));
        OnPropertyChanged(nameof(ShowWorkspaceLabel));
    }

    [RelayCommand]
    private void CloseReferenceAssistant()
    {
        deviceReference?.CloseAssistantCommand.Execute(null);
        OnPropertyChanged(nameof(HasReferenceAssistant));
        OnPropertyChanged(nameof(ShowWorkspaceLabel));
    }
}

// One entry of the Layout workspace's picture strip: a picture of the device reference (a photo view, JoyPro's drawing,
// the class outline) or the layout file.
public sealed partial class WorkspacePictureChoice : ObservableObject
{
    public string Title { get; init; } = string.Empty;
    public ReferencePictureOption? Reference { get; init; }
    public bool IsLayoutFile { get; init; }

    [ObservableProperty] private bool isSelected;

    public string Tip => IsLayoutFile ? T("Your layout file (the cheat sheets use it)")
        : Reference?.Kind switch
        {
            "user" => "Your photo of the device",
            "vendor" => "The maker's photo, loaded on this PC only (never shipped)",
            "lineart" => "JoyPro's own drawing of this model, with JoyPro's positions of its controls",
            "schematic" => "JoyPro's drawing of this model: every part where it is drawn",
            _ => T("JoyPro's outline of this kind of device (no picture of the model yet)")
        };

    public override string ToString() => Title;
}
