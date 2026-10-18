using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Markup.Xaml;
using VideoQuality.App.Converters;

namespace VideoQuality.App.Localization;

/// <summary>
/// <c>Text="{l:Label}"</c>: the friendly label of the data context (an enum value, a VMAF model), read again
/// when the language changes.
/// </summary>
public sealed class LabelExtension : MarkupExtension
{
    private static readonly LabelConverter Labels = new();

    private static readonly IMultiValueConverter Convert = new FuncMultiValueConverter<object?, object?>(
        values => Labels.Convert(values.First(), typeof(string), null, CultureInfo.InvariantCulture));

    public override object ProvideValue(IServiceProvider serviceProvider) => new MultiBinding
    {
        Bindings = { new Binding("."), LocSource.RevisionBinding() },
        Converter = Convert,
    };
}

/// <summary>
/// <c>Text="{l:Key Path=TitleKey}"</c>: the text for a key a view model holds, read again when the
/// language changes.
/// </summary>
public sealed class KeyExtension : MarkupExtension
{
    private static readonly IMultiValueConverter Convert = new FuncMultiValueConverter<object?, string>(
        values => VideoQuality.Core.Localization.Loc.T($"{values.First()}"));

    public string Path { get; set; } = ".";

    public override object ProvideValue(IServiceProvider serviceProvider) => new MultiBinding
    {
        Bindings = { new Binding(Path), LocSource.RevisionBinding() },
        Converter = Convert,
    };
}
