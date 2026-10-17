using Avalonia.Markup.Xaml;
using VideoQuality.Core.Localization;

namespace VideoQuality.App.Localization;

/// <summary>
/// <c>Text="{l:L compare.reference}"</c>: the text for a key in the active language. The language
/// is fixed for the life of the process (Settings offers a restart), so this is a plain string.
/// </summary>
public sealed class LExtension(string key) : MarkupExtension
{
    public string Key { get; } = key;

    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.T(Key);
}
