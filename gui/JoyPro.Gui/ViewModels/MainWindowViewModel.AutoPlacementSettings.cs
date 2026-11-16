using System.Linq;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using JoyPro.Gui.Models;

namespace JoyPro.Gui.ViewModels;

// Keeps the choices of Auto Assign, the Setup wizard and Lay out with AI in ui-settings.json ("autoPlacement"), so they
// survive a restart (docs/auto-assign.md, docs/setup-wizard.md, docs/layout-with-ai.md).
public sealed partial class MainWindowViewModel
{
    public IReadOnlyList<string> PhaseLayerChoices => PhaseLayerChoice.All;

    // Phase layers on a latched mode switch for Auto Assign (--phase-layers on|off|auto).
    [ObservableProperty]
    private string autoAssignPhaseLayers = PhaseLayerChoice.Automatic;

    // The Setup wizard's choices (its view model is made new for every run).
    private AutoPlacementSettings wizardSettings = new();

    partial void OnAutoAssignMaxModifiersChanged(int value) => SaveAutoPlacement();

    partial void OnAutoAssignOverwriteChanged(bool value) => SaveAutoPlacement();

    partial void OnAutoAssignIncludeMissingChanged(bool value) => SaveAutoPlacement();

    partial void OnAutoAssignKeepMyLayoutChanged(bool value) => SaveAutoPlacement();

    partial void OnAutoAssignOnlySelectedChanged(bool value) => SaveAutoPlacement();

    partial void OnAutoAssignPhaseLayersChanged(string value) => SaveAutoPlacement();

    private void SaveAutoPlacement()
    {
        if (!loadingUiSettings)
            SaveUiSettings();
    }

    private void ApplyAutoPlacement(AutoPlacementSettings? settings)
    {
        if (settings == null)
            return;
        AutoAssignMaxModifiers = settings.AutoAssignMaxModifiers is >= 0 and <= 2 ? settings.AutoAssignMaxModifiers : 2;
        AutoAssignKeepMyLayout = settings.AutoAssignKeepMyLayout;
        AutoAssignOverwrite = settings.AutoAssignOverwrite;
        AutoAssignIncludeMissing = settings.AutoAssignIncludeMissing;
        AutoAssignOnlySelected = settings.AutoAssignOnlySelected;
        AutoAssignPhaseLayers = PhaseLayerChoice.FromArgument(settings.AutoAssignPhaseLayers);
        wizardSettings = new AutoPlacementSettings
        {
            WizardModifiers = settings.WizardModifiers is >= 0 and <= 4 ? settings.WizardModifiers : 0,
            WizardPreferFewer = settings.WizardPreferFewer,
            WizardRealism = settings.WizardRealism is >= 0 and <= 100 ? settings.WizardRealism : 50,
            WizardPhaseLayers = PhaseLayerChoice.ToArgument(PhaseLayerChoice.FromArgument(settings.WizardPhaseLayers)),
            WizardHeadTracking = HeadTrackingChoice.Normalize(settings.WizardHeadTracking)
        };
        loadingHeadTracking = true;
        HeadTrackingAnswer = wizardSettings.WizardHeadTracking;
        loadingHeadTracking = false;
        ApplyLayoutSettings(settings);
    }

    private AutoPlacementSettings AutoPlacementForSave()
    {
        AutoPlacementSettings settings = new()
        {
            AutoAssignMaxModifiers = AutoAssignMaxModifiers,
            AutoAssignKeepMyLayout = AutoAssignKeepMyLayout,
            AutoAssignOverwrite = AutoAssignOverwrite,
            AutoAssignIncludeMissing = AutoAssignIncludeMissing,
            AutoAssignOnlySelected = AutoAssignOnlySelected,
            AutoAssignPhaseLayers = PhaseLayerChoice.ToArgument(AutoAssignPhaseLayers),
            WizardModifiers = wizardSettings.WizardModifiers,
            WizardPreferFewer = wizardSettings.WizardPreferFewer,
            WizardRealism = wizardSettings.WizardRealism,
            WizardPhaseLayers = wizardSettings.WizardPhaseLayers,
            WizardHeadTracking = wizardSettings.WizardHeadTracking
        };
        LayoutSettingsForSave(settings);
        return settings;
    }

    // The wizard reports its choices when they change; the next wizard starts with them.
    private void WizardSettingsChanged(SetupWizardViewModel wizard)
    {
        wizardSettings.WizardModifiers = wizard.Modifiers;
        wizardSettings.WizardPreferFewer = wizard.PreferFewer;
        wizardSettings.WizardRealism = wizard.Realism;
        wizardSettings.WizardPhaseLayers = PhaseLayerChoice.ToArgument(wizard.PhaseLayers);
        if (wizardSettings.WizardHeadTracking != wizard.HeadTrackingAnswer)
        {
            wizardSettings.WizardHeadTracking = wizard.HeadTrackingAnswer;
            loadingHeadTracking = true;
            HeadTrackingAnswer = wizard.HeadTrackingAnswer;
            loadingHeadTracking = false;
        }
        SaveAutoPlacement();
    }

    // ---- Settings > Looking around: the head-tracking answer (the same as the wizard's question) ----

    private bool loadingHeadTracking;

    // "yes", "no" or empty (not asked yet).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeadTrackingYes))]
    [NotifyPropertyChangedFor(nameof(HeadTrackingNo))]
    private string headTrackingAnswer = string.Empty;

    public bool HeadTrackingYes
    {
        get => HeadTrackingAnswer == HeadTrackingChoice.Yes;
        set
        {
            if (value)
                HeadTrackingAnswer = HeadTrackingChoice.Yes;
        }
    }

    public bool HeadTrackingNo
    {
        get => HeadTrackingAnswer == HeadTrackingChoice.No;
        set
        {
            if (value)
                HeadTrackingAnswer = HeadTrackingChoice.No;
        }
    }

    // After a change in Settings: the sims set up before follow the answer only when they are set up again.
    [ObservableProperty]
    private string headTrackingChangedNote = string.Empty;

    public bool HasHeadTrackingChangedNote => HeadTrackingChangedNote.Length > 0;

    partial void OnHeadTrackingChangedNoteChanged(string value) => OnPropertyChanged(nameof(HasHeadTrackingChangedNote));

    // The installed sims the answer concerns.
    private List<string> HeadTrackingSims() => GameCards.Where(c => c.Installed && HeadTrackingChoice.Concerns(c.GameId)).Select(c => c.GameId).ToList();

    partial void OnHeadTrackingAnswerChanged(string value)
    {
        if (loadingHeadTracking || wizardSettings.WizardHeadTracking == value)
            return;
        wizardSettings.WizardHeadTracking = value;
        SaveAutoPlacement();
        // The answer belongs to the open profile too: Auto Group and the suggestions follow it (engine profile head-tracking).
        if (value.Length > 0 && !string.IsNullOrWhiteSpace(ProfilePath))
            _ = RunEngineAsync(["profile", "head-tracking", value], includeProfile: true, mutatesProfile: true, busyLabel: string.Empty, background: true, countsAsBusy: false);
        List<string> names = GameCards.Where(c => c.Installed && HeadTrackingChoice.Concerns(c.GameId)).Select(c => c.Name).ToList();
        HeadTrackingChangedNote = names.Count == 0 ? string.Empty
            : T("Set up {0} again so the view buttons follow your answer.", string.Join(", ", names));
    }

    // Opens the Setup wizard for the sims the answer concerns.
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void SetUpAgainForHeadTracking()
    {
        HeadTrackingChangedNote = string.Empty;
        SetupWizard = CreateSetupWizard(HeadTrackingSims());
        OnPropertyChanged(nameof(SetupWizard));
        SetupWizardRequested?.Invoke();
    }
}
