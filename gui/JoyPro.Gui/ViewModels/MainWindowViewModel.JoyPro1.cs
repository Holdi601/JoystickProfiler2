using System;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using JoyPro.Gui.Services;
using JoyPro.Gui.Services.Help;

namespace JoyPro.Gui.ViewModels;

// JoyPro 1's last profile (Documents\JoyPro\last.pr0file, the one JoyPro 1 opened last; the support file knows the same
// place): JoyPro 2 opens it as it is. The welcome suggests the "I used JoyPro 1" path when it is there, and that tour's
// card offers it. JoyPro opens a copy in its own profiles folder, so JoyPro 1's file stays untouched for JoyPro 1.
public partial class MainWindowViewModel
{
    // JOYPRO_JOYPRO1_DIR (tests), else Documents\JoyPro (under the test sandbox: its Documents folder).
    internal static string JoyPro1ProfileFile()
    {
        string? overridden = Environment.GetEnvironmentVariable("JOYPRO_JOYPRO1_DIR");
        string folder = string.IsNullOrWhiteSpace(overridden)
            ? Path.Combine(UserFolders.Get(Environment.SpecialFolder.MyDocuments), "JoyPro")
            : overridden;
        return Path.Combine(folder, "last.pr0file");
    }

    public bool HasJoyPro1Profile
    {
        get
        {
            try
            {
                return File.Exists(JoyPro1ProfileFile());
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    // The tour card's button for the step's action (TourStep.Action).
    public bool TourShowsJoyPro1Offer => CurrentTourStep?.Action == TourActions.OpenJoyPro1Profile && HasJoyPro1Profile;

    [RelayCommand]
    private async Task OpenJoyPro1ProfileAsync()
    {
        string source = JoyPro1ProfileFile();
        if (!File.Exists(source))
        {
            StatusText = T("No JoyPro 1 profile found in {0}.", Path.GetDirectoryName(source) ?? source);
            return;
        }
        string copy;
        try
        {
            string folder = ProfilesFolder();
            Directory.CreateDirectory(folder);
            copy = Path.Combine(folder, "From JoyPro 1.pr0file");
            // A copy made before is opened again (the user may have worked on it); JoyPro 1's file is never written.
            if (!File.Exists(copy))
                File.Copy(source, copy);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = T("JoyPro 1's profile could not be copied: {0}", ex.Message);
            return;
        }
        await FlushAutosaveAsync();
        ProfilePath = copy;
        await LoadProfileCommand.ExecuteAsync(null);
        StatusText = T("Opened a copy of your JoyPro 1 profile ({0}). JoyPro 1's own file stays as it is.", copy);
    }
}
