using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace JoyPro.Gui.ViewModels;

// A question before something that changes game files or the profile and is hard to take back (restore a backup,
// clean export, remove a device, merge profiles, move DCS defaults). The main window answers it with a dialog
// (ConfirmWindow); without a window (tests, headless) nothing asks and the action runs, as on the command line.
// A question with a DontAskKey shows "Do not ask again": ticked and answered yes, it is not asked again until
// Settings > Confirmations switches it back on (list-preferences.json, MainWindowViewModel.Confirmations.cs).
public sealed record ConfirmRequest(string Title, string Message, string ConfirmLabel, bool IsDanger)
{
    public string DontAskKey { get; init; } = string.Empty;

    // Set by the dialog: "Do not ask again" was ticked.
    public bool DontAskAgain { get; set; }

    // A third answer between the confirm button and Cancel ("Export first"); it is the default button then.
    public string SecondaryLabel { get; init; } = string.Empty;

    // Set by the dialog: the third answer was picked (the handler returns true then).
    public bool SecondaryChosen { get; set; }
}

public enum ConfirmChoice
{
    Confirm,
    Secondary,
    Cancel
}

public sealed partial class MainWindowViewModel
{
    public Func<ConfirmRequest, Task<bool>>? ConfirmHandler { get; set; }

    // The questions asked so far (newest last), for tests and the problem report.
    internal List<ConfirmRequest> AskedConfirmations { get; } = [];

    internal async Task<bool> ConfirmAsync(string title, string message, string confirmLabel = "Continue", bool isDanger = false, string dontAskKey = "")
    {
        if (dontAskKey.Length > 0 && !ListPrefs.ShouldAsk(dontAskKey))
            return true;
        ConfirmRequest request = new(title, message, T(confirmLabel), isDanger) { DontAskKey = dontAskKey };
        AskedConfirmations.Add(request);
        if (ConfirmHandler == null)
            return true;
        bool ok = await ConfirmHandler(request);
        if (!ok)
            StatusText = T("Cancelled: {0}.", title);
        else if (dontAskKey.Length > 0 && request.DontAskAgain)
            SetConfirmationAsked(dontAskKey, false);
        return ok;
    }

    // Three answers: the confirm button (the action), the secondary one (another way, e.g. "Export first") and Cancel.
    // Without a window (tests, headless) the action runs, as on the command line.
    internal async Task<ConfirmChoice> ConfirmChoiceAsync(string title, string message, string confirmLabel, string secondaryLabel, string dontAskKey = "")
    {
        if (dontAskKey.Length > 0 && !ListPrefs.ShouldAsk(dontAskKey))
            return ConfirmChoice.Confirm;
        ConfirmRequest request = new(title, message, confirmLabel, IsDanger: false) { SecondaryLabel = secondaryLabel, DontAskKey = dontAskKey };
        AskedConfirmations.Add(request);
        if (ConfirmHandler == null)
            return ConfirmChoice.Confirm;
        bool ok = await ConfirmHandler(request);
        if (!ok)
            return ConfirmChoice.Cancel;
        if (request.SecondaryChosen)
            return ConfirmChoice.Secondary;
        // "Do not ask again" counts for the action itself (the confirm button), as in every other question.
        if (dontAskKey.Length > 0 && request.DontAskAgain)
            SetConfirmationAsked(dontAskKey, false);
        return ConfirmChoice.Confirm;
    }
}
