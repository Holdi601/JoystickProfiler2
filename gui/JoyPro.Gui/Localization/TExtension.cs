using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Markup.Xaml;

namespace JoyPro.Gui.Localization;

// XAML: a text in the user's language, switching live with the language (docs/localization.md).
//   xmlns:l="using:JoyPro.Gui.Localization"
//   Text="{l:T 'Bind here'}"
//   Text="{l:T 'Start: {0}', Arg0={Binding SourceLabel}}"           (a format text; Arg0..Arg3)
//   Text="{l:T '{0} bind', Plural='{0} binds', Count={Binding N}}"  (a plural text: the count is {0})
// On a property that is not an Avalonia property (a CLR property) the text is put in once.
public sealed class TExtension : MarkupExtension
{
    public TExtension()
    {
    }

    public TExtension(string text)
    {
        Text = text;
    }

    // A text that starts with a brace is written '{}{0} ...' in XAML (else it reads as a markup extension): the "{}"
    // is not part of the key.
    public string Text
    {
        get => text;
        set => text = Unescape(value) ?? string.Empty;
    }

    // The English plural text of a count text (Text is the singular); Count gives the number.
    public string? Plural
    {
        get => plural;
        set => plural = Unescape(value);
    }

    private string text = string.Empty;
    private string? plural;

    private static string? Unescape(string? value) =>
        value != null && value.StartsWith("{}", StringComparison.Ordinal) ? value[2..] : value;

    [AssignBinding]
    public IBinding? Count { get; set; }

    [AssignBinding]
    public IBinding? Arg0 { get; set; }

    [AssignBinding]
    public IBinding? Arg1 { get; set; }

    [AssignBinding]
    public IBinding? Arg2 { get; set; }

    [AssignBinding]
    public IBinding? Arg3 { get; set; }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        bool avaloniaTarget = serviceProvider.GetService(typeof(IProvideValueTarget)) is IProvideValueTarget target
            && target.TargetProperty is AvaloniaProperty;
        // A setter's value (a style) takes a binding too.
        bool setterTarget = serviceProvider.GetService(typeof(IProvideValueTarget)) is IProvideValueTarget t2
            && t2.TargetObject is Avalonia.Styling.Setter;
        IBinding[] args = new[] { Arg0, Arg1, Arg2, Arg3 }.TakeWhile(a => a != null).Select(a => a!).ToArray();
        if (!avaloniaTarget && !setterTarget)
            return Plural != null ? Loc.Plural(Text, Plural, 1) : Loc.T(Text);

        Binding text = new(nameof(LocText.Value)) { Source = Loc.Observable(Text), Mode = BindingMode.OneWay };
        if (Plural == null && args.Length == 0)
            return text;

        MultiBinding multi = new() { Converter = new FormatConverter(Text, Plural), Mode = BindingMode.OneWay };
        multi.Bindings.Add(text);
        if (Plural != null)
            multi.Bindings.Add(Count ?? args.FirstOrDefault() ?? new Binding { Source = 0 });
        foreach (IBinding arg in args)
            multi.Bindings.Add(arg);
        return multi;
    }

    // values[0]: the (translated) text, which only changes with the language; the plural form is looked up again from
    // the English texts. Then the count (plural texts) and the arguments.
    private sealed class FormatConverter(string text, string? plural) : IMultiValueConverter
    {
        public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
        {
            if (plural != null)
            {
                long count = ToCount(values.Count > 1 ? values[1] : null);
                object?[] rest = values.Skip(2).Select(Plain).ToArray();
                return rest.Length == 0 ? Loc.Plural(text, plural, count) : Loc.Plural(text, plural, count, rest);
            }
            object?[] args = values.Skip(1).Select(Plain).ToArray();
            return Loc.T(text, args);
        }

        private static object? Plain(object? value) =>
            value is UnsetValueType || value is BindingNotification ? string.Empty : value;

        private static long ToCount(object? value)
        {
            try
            {
                return value switch
                {
                    null => 0,
                    UnsetValueType => 0,
                    IConvertible c => System.Convert.ToInt64(c, CultureInfo.InvariantCulture),
                    _ => 0
                };
            }
            catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
            {
                return 0;
            }
        }
    }
}
