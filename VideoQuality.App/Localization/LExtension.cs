using Avalonia.Markup.Xaml;

namespace VideoQuality.App.Localization;

/// <summary>
/// <c>Text="{l:L compare.reference}"</c>: the text for a key in the active language, as a binding that
/// follows language changes.
/// </summary>
public sealed class LExtension(string key) : MarkupExtension
{
    public string Key { get; } = key;

    public override object ProvideValue(IServiceProvider serviceProvider) => LocSource.Text(Key);
}
