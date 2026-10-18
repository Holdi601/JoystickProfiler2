using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using JoyPro.DeviceReference;

namespace JoyPro.Gui.Models;

// One device on the Start page ("Your devices") and in Settings > Devices: connected and/or used by the profile, with
// the model the device reference found for it (docs/device-reference.md: VID:PID and HID name; a configured grip on a
// base), the picture the Layout page shows (the user's layout or photo, the maker's photo, else JoyPro's drawing) and
// how many axes, buttons and hats it has.
public sealed partial class DeviceCardItem : ObservableObject
{
    public DeviceCardItem(string device, string title, string model, string kind, int axes, int buttons, int hats, bool connected, bool inProfile)
    {
        Device = device;
        // The instance GUID ({...}) means nothing to the user; the tooltip shows the full name.
        string name = title.Length > 0 && title != device ? title : device;
        int guid = name.LastIndexOf(" {", System.StringComparison.Ordinal);
        DeviceName = guid > 0 && name.EndsWith('}') ? name[..guid] : name;
        Model = model;
        Kind = kind;
        Axes = axes;
        Buttons = buttons;
        Hats = hats;
        Connected = connected;
        InProfile = inProfile;
    }

    // The device name the games and the profile use (DirectInput name with its instance GUID).
    public string Device { get; }

    // What Windows calls the device (HID product name, without the GUID).
    public string DeviceName { get; }

    // The model from the device catalog ("Virpil VPC MongoosT-50CM2 Throttle"), else the device's name.
    public string Title => ModelName.Length > 0 ? ModelName : DeviceName;

    // The engine's model name (hud devices).
    public string Model { get; }

    // The device reference's model (catalog name and id); empty when it is not in the catalog.
    public string ModelName { get; init; } = string.Empty;

    public string ModelId { get; init; } = string.Empty;

    public bool HasModel => Model.Length > 0 && Model != Title;

    // The Windows name under the model name, when it says something else.
    public bool ShowsDeviceName => DeviceName.Length > 0 && DeviceName != Title;

    // stick, throttle, pedals, ... (empty when unknown).
    public string Kind { get; }

    // The chip: "Stick", "Throttle", "Pedals", "Panel", "FFB base", "Virtual", ...
    public string KindLabel { get; init; } = string.Empty;

    public bool HasKindLabel => KindLabel.Length > 0;

    // A virtual joystick (vJoy).
    public bool IsVirtual { get; init; }

    // A base that takes several grips and none is set yet: the card shows the base and asks for the grip.
    public bool NeedsGrip { get; init; }

    // Interfaces of the same device (a keyboard reports several) shown as this one card.
    public int Interfaces { get; init; } = 1;

    public int Axes { get; }

    public int Buttons { get; }

    public int Hats { get; }

    public bool Connected { get; }

    public bool NotConnected => !Connected;

    public bool InProfile { get; }

    public string InputsText => Connected ? T("{0} axes · {1} buttons · {2} hats", Axes, Buttons, Hats) : T("Not connected now");

    public string ProfileText => InProfile ? T("Used in your profile") : T("Not in your profile yet");

    public string Tooltip => Device + (ModelId.Length > 0 ? T("\nModel: {0} ({1})", ModelName, ModelId) : string.Empty) +
                             (PictureSource.Length > 0 ? T("\nPicture: {0}", PictureSource) : string.Empty) +
                             (NeedsGrip ? T("\nWhich grip is on the base? Pick it on the Layout page.") : string.Empty);

    // The user's layout picture or photo, JoyPro's line drawing of the model, or the maker's photo (null = JoyPro's
    // schematic drawing or nothing).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPicture), nameof(HasNoPicture), nameof(Tooltip))]
    private Avalonia.Media.IImage? picture;

    // JoyPro's drawing of the model (shown while there is no picture).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPicture), nameof(HasNoPicture), nameof(Tooltip))]
    private DeviceSchematic? schematic;

    // Where the picture comes from: "Your layout", "Your photo", "Maker's photo", "JoyPro drawing" (empty = none).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Tooltip))]
    private string pictureSource = string.Empty;

    // JoyPro found the maker's photo of this model and the user has not answered yet (DevicePhotoConsent): the card
    // asks "Download it and show it here?" with Yes / No / Always / Never.
    [ObservableProperty]
    private bool asksPhotoConsent;

    [ObservableProperty]
    private string photoConsentText = string.Empty;

    // An analog keyboard (or a keyboard the catalog knows): typing moves its axes, so follow input and recording leave
    // them alone unless UsesAxes (Settings > Devices, "Use this device's axes for follow input and recording").
    public bool IsKeyboardLike { get; init; }

    [ObservableProperty]
    private bool usesAxes;

    public bool HasPicture => Picture != null || Schematic != null;

    public bool HasNoPicture => !HasPicture;

    public override string ToString() => Title;
}
