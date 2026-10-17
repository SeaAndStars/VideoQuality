using System.Globalization;
using Avalonia.Data.Converters;
using VideoQuality.App.ViewModels;
using VideoQuality.Core.Imaging;
using VideoQuality.Core.Scoring;

namespace VideoQuality.App.Converters;

/// <summary>Friendly labels for the enums shown in combo boxes.</summary>
public sealed class LabelConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        CompareMode.SideBySide => "Side by side",
        CompareMode.Wipe => "Wipe",
        CompareMode.Flicker => "Flicker",
        CompareMode.Difference => "Difference",
        DifferenceChannels.Luma => "Luma",
        DifferenceChannels.Chroma => "Chroma",
        DifferenceChannels.Both => "Luma + chroma",
        VmafBackend.Auto => "Auto",
        VmafBackend.Cpu => "CPU",
        VmafBackend.Cuda => "CUDA",
        _ => value?.ToString(),
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
