using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace JoyPro.Gui.Models;

// One choice in plain words for a setting the engine takes as a keyword (export mode "merge-overwrite", merge side
// "right", ...): Key goes to the engine, Title and Description are what the user reads, Icon is a symbol of
// Styles/Icons.axaml (null when no application is running, e.g. in tests).
public sealed class ChoiceOption
{
    public ChoiceOption(string key, string title, string description, string iconKey = "Icon.Info", bool isRecommended = false, bool isRisky = false)
    {
        Key = key;
        rawTitle = title;
        rawDescription = description;
        IconKey = iconKey;
        IsRecommended = isRecommended;
        IsRisky = isRisky;
    }

    public string Key { get; }

    private readonly string rawTitle;
    private readonly string rawDescription;

    // In the language shown (the texts are English keys: N("...") where the options are made).
    public string Title => T(rawTitle);

    public string Description => T(rawDescription);

    public string IconKey { get; }

    public bool IsRecommended { get; }

    public bool IsRisky { get; }

    public Geometry? Icon => IconLookup.Find(IconKey);

    public override string ToString() => Title;
}

// Finds a symbol of Styles/Icons.axaml by its key (for icons chosen in code).
public static class IconLookup
{
    public static Geometry? Find(string key)
    {
        if (string.IsNullOrEmpty(key) || Application.Current == null)
            return null;
        try
        {
            return Application.Current.TryFindResource(key, out object? value) ? value as Geometry : null;
        }
        catch (System.InvalidOperationException)
        {
            // Asked from a thread other than the application's (tests): no symbol.
            return null;
        }
    }
}
