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
            WizardPhaseLayers = PhaseLayerChoice.ToArgument(PhaseLayerChoice.FromArgument(settings.WizardPhaseLayers))
        };
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
            WizardPhaseLayers = wizardSettings.WizardPhaseLayers
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
        SaveAutoPlacement();
    }
}
