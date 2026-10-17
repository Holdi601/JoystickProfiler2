using CommunityToolkit.Mvvm.ComponentModel;

namespace JoyPro.Gui.Models;

public sealed partial class SelectableTagItem : ObservableObject
{
    public SelectableTagItem(string name, bool isSelected = false, string? value = null)
    {
        Name = name;
        Value = string.IsNullOrWhiteSpace(value) ? name : value.Trim();
        this.isSelected = isSelected;
    }

    public string Name { get; }

    public string Value { get; }

    // What a chip shows: the engine's "<none>" (relations without a group) in words.
    public string Label => Name == "<none>" ? T("No group") : Name;

    [ObservableProperty]
    private bool isSelected;
}
