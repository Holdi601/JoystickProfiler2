using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

using JoyPro.Gui.Localization;

namespace JoyPro.Gui.Services;

// The tray host's texts (JoyPro.Watch.WatchText) in the language the GUI shows: written to <settings>\tray-strings.json
// whenever the language is set, so the tray needs no string tables of its own (docs/localization.md).
internal static class TrayStrings
{
    internal static Dictionary<string, string> Build() => new(StringComparer.Ordinal)
    {
        ["A game changed its controls"] = T("A game changed its controls"),
        ["{0} changed {1} control JoyPro manages. Click here to take it over or ignore it in JoyPro2."] = T("{0} changed {1} control JoyPro manages. Click here to take it over or ignore it in JoyPro2."),
        ["{0} changed {1} controls JoyPro manages. Click here to take them over or ignore them in JoyPro2."] = T("{0} changed {1} controls JoyPro manages. Click here to take them over or ignore them in JoyPro2."),
        ["JoyPro2 is open and watches the games itself"] = T("JoyPro2 is open and watches the games itself"),
        ["JoyPro2: {0} changed controls - click to review"] = T("JoyPro2: {0} changed controls - click to review"),
        ["JoyPro2: {0} games changed controls - click to review"] = T("JoyPro2: {0} games changed controls - click to review"),
        ["JoyPro2: not watching game configs"] = T("JoyPro2: not watching game configs"),
        ["JoyPro2: no game to watch yet"] = T("JoyPro2: no game to watch yet"),
        ["JoyPro2: watching {0} game config"] = T("JoyPro2: watching {0} game config"),
        ["JoyPro2: watching {0} game configs"] = T("JoyPro2: watching {0} game configs"),
        ["Review the changes made in {0} ({1})"] = T("Review the changes made in {0} ({1})"),
        ["Review the changes made in {0} games"] = T("Review the changes made in {0} games"),
        ["Watching game configs (JoyPro2 is open: set it there)"] = T("Watching game configs (JoyPro2 is open: set it there)"),
        ["Watching game configs"] = T("Watching game configs"),
        ["Open JoyPro2"] = T("Open JoyPro2"),
        ["Review"] = T("Review"),
        ["Exit"] = T("Exit")
    };

    internal static void Write(string settingsFolder)
    {
        try
        {
            Directory.CreateDirectory(settingsFolder);
            string file = Path.Combine(settingsFolder, Watch.WatchText.FileName);
            if (Loc.Current == Languages.English)
            {
                if (File.Exists(file))
                    File.Delete(file);
                return;
            }
            File.WriteAllText(file, JsonSerializer.Serialize(Build()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The tray stays English.
        }
    }
}
