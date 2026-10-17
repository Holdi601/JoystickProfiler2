using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace JoyPro.Gui.Localization;

// XAML: an English value (a filter choice such as "All") shown in the user's language; the value itself stays English.
//   Text="{Binding Converter={x:Static l:TConverter.Instance}}"
public sealed class TConverter : IValueConverter
{
    public static readonly TConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string text ? Loc.T(text) : value;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
