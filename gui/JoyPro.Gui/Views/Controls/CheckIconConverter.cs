using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace JoyPro.Gui.Views.Controls;

// The check card's kind ("error", "warning", "note") -> its icon (Styles/Icons.axaml): one PathIcon per light card
// instead of three (thousands of cards can come).
public sealed class CheckIconConverter : IValueConverter
{
    public static readonly CheckIconConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string key = (value as string) switch
        {
            "error" => "Icon.Error",
            "warning" => "Icon.Warning",
            _ => "Icon.Info"
        };
        return Application.Current != null && Application.Current.TryGetResource(key, Application.Current.ActualThemeVariant, out object? found) ? found as Geometry : null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
