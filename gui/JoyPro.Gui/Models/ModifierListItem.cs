using CommunityToolkit.Mvvm.ComponentModel;

namespace JoyPro.Gui.Models;

public sealed partial class ModifierListItem : ObservableObject
{
    public ModifierListItem(
        string name,
        int usageCount,
        string usedByPreview,
        string usedByFull,
        string primaryDevice = "",
        string primaryInput = "",
        bool isPending = false)
    {
        Name = name ?? string.Empty;
        UsageCount = usageCount;
        UsedByPreview = usedByPreview ?? string.Empty;
        UsedByFull = usedByFull ?? string.Empty;
        PrimaryDevice = primaryDevice ?? string.Empty;
        PrimaryInput = primaryInput ?? string.Empty;
        IsPending = isPending;
    }

    public string Name { get; }

    public int UsageCount { get; }

    public string UsedByPreview { get; }

    public string UsedByFull { get; }

    public string PrimaryDevice { get; }

    public string PrimaryInput { get; }

    public bool IsPending { get; }

    public string DisplayName => IsPending ? Name + " (temp)" : Name;

    public string DeviceInputPreview
    {
        get
        {
            string device = string.IsNullOrWhiteSpace(PrimaryDevice) ? "<unknown>" : PrimaryDevice.Trim();
            string input = string.IsNullOrWhiteSpace(PrimaryInput) ? "<unknown>" : PrimaryInput.Trim();
            return device + " :: " + input;
        }
    }

    // For the modifier cards: the shift button as a key cap and its device as a chip.
    public string DeviceShort => DisplayText.ShortDevice(PrimaryDevice);

    public string InputLabel => DisplayText.Input(PrimaryInput);

    public bool HasInput => InputLabel.Length > 0;

    public string UsageText => Plural("{0} bind", "{0} binds", UsageCount);

    [ObservableProperty]
    private bool isSelected;
}
