using VideoQuality.Core.Tools;

namespace VideoQuality.Core.Encode;

/// <summary>
/// hevc_nvenc p7 with the ultra-high-quality tune and full-resolution two-pass analysis. At constant quality
/// 26.25 it matched an x265 medium CRF 18 encode's size on a 1080p anime episode with a higher VMAF on every
/// statistic. The GreenTea rules require x265, so this is for comparison and private encodes.
/// </summary>
public static class NvencUhq
{
    public const double Quality = 26.25;

    public const string Arguments = "-multipass fullres -maxrate 8000k -bufsize 16000k";

    /// <summary>The preset with its video swapped for NVENC UHQ at <paramref name="quality"/>; frame rate and depth are kept.</summary>
    public static EncodePreset Apply(EncodePreset preset, double quality) => preset with
    {
        Video = preset.Video with
        {
            Encoder = "hevc_nvenc",
            Mode = RateControl.Quality,
            Quality = quality,
            Preset = "p7",
            Tune = "uhq",
            Profile = null,
            EncoderParams = null,
        },
        Custom = preset.Custom with { Video = Arguments },
    };

    /// <summary>The UHQ tune is missing from <paramref name="encoder"/> (NVENC headers before SDK 12.2).</summary>
    public static bool Unsupported(EncodePreset preset, EncoderTool? encoder) =>
        preset.Video.Tune == "uhq" && encoder is { SupportsNvencUhq: false };

    /// <summary>The preset as <paramref name="encoder"/> can run it: <c>-tune hq</c> where UHQ is missing.</summary>
    public static EncodePreset ForEncoder(EncodePreset preset, EncoderTool? encoder) =>
        Unsupported(preset, encoder) ? preset with { Video = preset.Video with { Tune = "hq" } } : preset;
}
