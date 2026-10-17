using System.Globalization;
using Avalonia.Data.Converters;
using VideoQuality.App.ViewModels;
using VideoQuality.Core.Imaging;
using VideoQuality.Core.Scoring;
using VideoQuality.Core.Localization;

namespace VideoQuality.App.Converters;

/// <summary>Friendly labels for the enums shown in combo boxes.</summary>
public sealed class LabelConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        CompareMode.SideBySide => Loc.T("label.mode.sideBySide"),
        CompareMode.Wipe => Loc.T("label.mode.wipe"),
        CompareMode.Flicker => Loc.T("label.mode.flicker"),
        CompareMode.Difference => Loc.T("label.mode.difference"),
        DifferenceChannels.Luma => Loc.T("label.channel.luma"),
        DifferenceChannels.Chroma => Loc.T("label.channel.chroma"),
        DifferenceChannels.Both => Loc.T("label.channel.both"),
        VmafBackend.Auto => Loc.T("label.backend.auto"),
        VmafBackend.Cpu => "CPU",
        VmafBackend.Cuda => "CUDA",
        string model => VmafModels.DisplayName(model),
        Enum other when Loc.Has($"label.{other.GetType().Name}.{other}") => Loc.T($"label.{other.GetType().Name}.{other}"),
        _ => value?.ToString(),
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
