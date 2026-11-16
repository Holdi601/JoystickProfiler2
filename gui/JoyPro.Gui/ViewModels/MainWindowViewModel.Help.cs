using System;
using System.Collections.Generic;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JoyPro.Gui.Services;
using JoyPro.Gui.Services.Help;

namespace JoyPro.Gui.ViewModels;

// F1's help mode (docs/ui/help-and-tours.md): F1 or the Help button's Help mode switches it on, F1 or Esc off. While it is on, a click
// on a control does not run it: the popup next to it says what it does, where it fits in the bigger picture and what
// happens after, with a link to the control's wiki entry. The window finds the control and its text (HelpResolver).
public sealed partial class MainWindowViewModel
{
    [ObservableProperty]
    private bool isHelpModeOn;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHelpPopupOpen))]
    private HelpTopic? helpTopic;

    public bool IsHelpPopupOpen => HelpTopic != null;

    // The popup's details (what it does in full, where it fits, what happens next, the words): open by default, closed
    // with easy words (one plain sentence first, the rest behind "More details").
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HelpDetailsLabel))]
    private bool isHelpDetailsOpen = true;

    public string HelpDetailsLabel => IsHelpDetailsOpen ? T("Fewer details") : T("More details");

    [RelayCommand]
    private void ToggleHelpDetails() => IsHelpDetailsOpen = !IsHelpDetailsOpen;

    // The open page's slug (the wiki's GUI page), for the help popup and "Tour of this page".
    public string CurrentHelpPage => HelpCatalog.PageOf(CurrentStage);

    public string CurrentPageTourLabel => T("Tour of the {0} page", T(HelpCatalog.Page(CurrentHelpPage).Title));

    // The last URL a wiki link opened (tests).
    internal string LastOpenedHelpUrl { get; private set; } = string.Empty;

    partial void OnIsHelpModeOnChanged(bool value)
    {
        if (!value)
            HelpTopic = null;
        StatusText = value
            ? T("Help mode: click anything to see what it does. F1 or Esc ends it.")
            : T("Help mode ended.");
    }

    [RelayCommand]
    private void ToggleHelpMode() => IsHelpModeOn = !IsHelpModeOn;

    [RelayCommand]
    private void EndHelpMode() => IsHelpModeOn = false;

    [RelayCommand]
    private void CloseHelpPopup() => HelpTopic = null;

    public void ShowHelp(HelpTopic topic)
    {
        // The Virtual Joystick page of a player whose games are all on the pretend controller is called that (as its
        // title and the bar button), and its next step is the pretend controller's only.
        if (topic.Key == "page:vjoy" && VirtualJoystickPadOnly)
            topic = topic with
            {
                Title = T("Pretend controller"),
                Next = T("Start the game and wait for its title screen. Then press Alt+Tab and click Start here.")
            };
        IsHelpDetailsOpen = !EasyWords;
        HelpTopic = topic;
    }

    [RelayCommand]
    private void OpenHelpWiki()
    {
        string url = HelpTopic?.WikiUrl ?? HelpCatalog.WikiUrl(CurrentHelpPage);
        OpenWikiUrl(url);
    }

    // The help menu's "Open the wiki": the wiki page of the open page.
    [RelayCommand]
    private void OpenPageWiki() => OpenWikiUrl(HelpCatalog.WikiUrl(CurrentHelpPage));

    private void OpenWikiUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps)
            return;
        LastOpenedHelpUrl = uri.AbsoluteUri;
        try
        {
            ExternalProcess.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            StatusText = T("The wiki could not be opened: {0}", uri.AbsoluteUri);
        }
    }

    private void OnHelpStageChanged()
    {
        OnPropertyChanged(nameof(CurrentHelpPage));
        OnPropertyChanged(nameof(CurrentPageTourLabel));
        HelpTopic = null;
    }
}
