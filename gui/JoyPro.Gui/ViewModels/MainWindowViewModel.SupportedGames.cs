using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;

namespace JoyPro.Gui.ViewModels;

// Start page, "Supported games" next to "Your games": how far JoyPro supports each game (docs/games/support.md). The
// engine writes the page as one HTML file (engine games support --html: the feature matrix from the code plus the test
// results it carries) into the temp folder, and the browser shows it; the docs are not part of an installed copy.
public sealed partial class MainWindowViewModel
{
    public static string SupportedGamesPagePath => Path.Combine(Path.GetTempPath(), "JoyPro2", "supported-games.html");

    [RelayCommand]
    private async Task OpenSupportedGamesAsync()
    {
        string page = SupportedGamesPagePath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(page)!);
            if (File.Exists(page))
                File.Delete(page);
        }
        catch (Exception ex)
        {
            StatusText = T("The supported games page could not be prepared: {0}", ex.Message);
            return;
        }
        EngineCommandResult? result = await RunEngineAsync(["games", "support", "--html", "--out", page], includeProfile: false, mutatesProfile: false,
            busyLabel: N("Writing the list of supported games..."), forceText: false);
        // Exit 1 = the built-in notes do not match the code (a developer build); the page is still written.
        if (result == null || !File.Exists(page))
        {
            StatusText = T("The list of supported games could not be written.");
            return;
        }
        try
        {
            ExternalProcess.Start(new ProcessStartInfo(page) { UseShellExecute = true })?.Dispose();
            StatusText = T("Supported games: opened in your browser.");
        }
        catch (Exception ex)
        {
            StatusText = T("The browser could not be opened ({0}). The page is here: {1}", ex.Message, page);
        }
    }
}
