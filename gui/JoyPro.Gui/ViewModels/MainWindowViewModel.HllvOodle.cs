using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JoyPro.Gui.Models;

namespace JoyPro.Gui.ViewModels;

// Hell Let Loose: Vietnam's command list and Oodle (docs/games/hll-vietnam.md "Oodle", user decision 2026-10-12): the
// game's input files are Oodle-compressed and the game ships no Oodle library of its own. When another installed game
// ships one, JoyPro asks once, when the HLL Vietnam list is built or updated, whether it may use that library on this
// PC to read HLL Vietnam's files (it belongs to its maker and is licensed with the other game; the use is private; JoyPro
// never copies or ships it). Yes passes refresh-db --oodle-dll <path> (the engine loads it from where it is, only for
// the refresh, and logs it); No (or no answer) keeps JoyPro's own list of the pad keys, which is all the Xbox-pad mode
// needs. The answer is remembered for that file; Settings > Games changes it. The game's own library needs no question.
public sealed partial class MainWindowViewModel
{
    // "yes", "no" or "" (not asked yet), and the library the answer is for.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HllvOodleAllowed), nameof(HllvOodleStatus))]
    private string hllvOodleAnswer = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HllvOodleStatus))]
    private string hllvOodleFile = string.Empty;

    // Settings > Games: the remembered answer as a switch (ticked = yes).
    public bool HllvOodleAllowed
    {
        get => HllvOodleAnswer == "yes";
        set
        {
            string answer = value ? "yes" : "no";
            if (answer == HllvOodleAnswer)
                return;
            HllvOodleAnswer = answer;
        }
    }

    public string HllvOodleStatus => HllvOodleAnswer.Length == 0
        ? T("Not asked yet: JoyPro asks when it builds the Hell Let Loose: Vietnam command list.")
        : (HllvOodleAnswer == "yes" ? T("Allowed") : T("Not allowed")) + (HllvOodleFile.Length > 0 ? ": " + HllvOodleFile : string.Empty);

    partial void OnHllvOodleAnswerChanged(string value)
    {
        if (!loadingUiSettings)
            SaveUiSettings();
    }

    partial void OnHllvOodleFileChanged(string value)
    {
        if (!loadingUiSettings)
            SaveUiSettings();
    }

    [RelayCommand]
    private void AskHllvOodleAgain()
    {
        HllvOodleAnswer = string.Empty;
        HllvOodleFile = string.Empty;
        StatusText = T("JoyPro asks again the next time it builds the Hell Let Loose: Vietnam command list.");
    }

    // Before a refresh of HLL Vietnam's list: asks about another game's Oodle library once and adds --oodle-dll after a yes.
    internal async Task AddHllvOodleConsentAsync(string game, List<string> args)
    {
        if (!string.Equals(game, "HLLVietnam", StringComparison.OrdinalIgnoreCase))
            return;
        EngineCommandResult? status = await RunEngineAsync(["hllv", "oodle"], includeProfile: false, mutatesProfile: false,
            busyLabel: N("Looking at Hell Let Loose: Vietnam's files..."), forceText: false);
        if (status == null || status.ExitCode != 0)
            return;
        string own, foreign, foreignGame, maker;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(ExtractJsonPayload(status.StandardOutput));
            own = ReadJsonString(doc.RootElement, "OwnDll");
            foreign = ReadJsonString(doc.RootElement, "ForeignDll");
            foreignGame = ReadJsonString(doc.RootElement, "ForeignGame");
            maker = ReadJsonString(doc.RootElement, "Maker");
        }
        catch (JsonException)
        {
            return;
        }
        // The game's own library: the engine uses it by itself, no question.
        if (own.Length > 0 || foreign.Length == 0)
            return;
        // An answer given in Settings before any library was named counts for the one found now.
        bool sameFile = HllvOodleFile.Length == 0 ? HllvOodleAnswer.Length > 0 : string.Equals(HllvOodleFile, foreign, StringComparison.OrdinalIgnoreCase);
        if (sameFile && HllvOodleFile.Length == 0)
            HllvOodleFile = foreign;
        if (!sameFile || HllvOodleAnswer.Length == 0)
        {
            // Without a window to ask in (tests, headless) nothing is assumed: no answer, JoyPro's own list.
            if (ConfirmHandler == null)
                return;
            string message = T("JoyPro found an Oodle library in another game installed on this PC: {0} ({1}, {2}).{3}{4}JoyPro can use it on this PC only to read Hell Let Loose: Vietnam's files, so its command list comes from the game itself. The library belongs to its maker ({5}) and is licensed with {6}. By agreeing you confirm that this use is private and for yourself only. JoyPro never copies or ships it.{7}{8}No: JoyPro uses its own list of the gamepad keys, which is all the Xbox-pad mode needs. You can change this in Settings > Games.", foreignGame, Path.GetFileName(foreign), foreign, Environment.NewLine, Environment.NewLine, (maker.Length > 0 ? maker : T("RAD Game Tools / Epic Games")), foreignGame, Environment.NewLine, Environment.NewLine);
            bool yes = await ConfirmHandler(new ConfirmRequest(T("Use the Oodle library of {0}?", foreignGame), message, T("Yes, use it"), false));
            AskedConfirmations.Add(new ConfirmRequest(T("Use the Oodle library of {0}?", foreignGame), message, T("Yes, use it"), false));
            HllvOodleFile = foreign;
            HllvOodleAnswer = yes ? "yes" : "no";
        }
        if (HllvOodleAnswer == "yes")
            args.AddRange(["--oodle-dll", foreign]);
    }
}
