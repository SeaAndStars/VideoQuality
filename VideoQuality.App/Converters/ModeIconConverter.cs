using System.Globalization;
using Avalonia.Data.Converters;
using FluentIcons.Common;
using VideoQuality.App.ViewModels;

namespace VideoQuality.App.Converters;

/// <summary>Icon for each compare mode in the segmented view switch.</summary>
public sealed class ModeIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        CompareMode.SideBySide => Symbol.SplitVertical,
        CompareMode.Wipe => Symbol.ArrowAutofitWidth,
        CompareMode.Flicker => Symbol.Flash,
        CompareMode.Difference => Symbol.ShapeExclude,
        _ => Symbol.Square,
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
