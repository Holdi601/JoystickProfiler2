using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using JoyPro.DeviceReference;

namespace JoyPro.Gui.Models;

// One view on the page of a device layout with several views (MultiViewItem with its rendered picture).
public sealed partial class MultiViewItemModel : ObservableObject
{
    public MultiViewItemModel(MultiViewItem item)
    {
        Item = item;
    }

    public MultiViewItem Item { get; }

    public string Title => Item.Title;

    public bool IsImage => Item.Image.Length > 0;

    public double X { get => Item.X; set { Item.X = value; OnPropertyChanged(); } }

    public double Y { get => Item.Y; set { Item.Y = value; OnPropertyChanged(); } }

    public double Scale { get => Item.Scale; set { Item.Scale = value; OnPropertyChanged(); } }

    public double Width { get => Item.Width; set { Item.Width = value; OnPropertyChanged(); } }

    public double Height { get => Item.Height; set { Item.Height = value; OnPropertyChanged(); } }

    // Arrange placed it anew.
    public void OnPlaced()
    {
        OnPropertyChanged(nameof(X));
        OnPropertyChanged(nameof(Y));
        OnPropertyChanged(nameof(Scale));
    }

    // The view as the engine drew it (its picture with tables and lines); null until rendered.
    [ObservableProperty]
    private Bitmap? image;
}
