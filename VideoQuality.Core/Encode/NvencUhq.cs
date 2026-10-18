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
}
