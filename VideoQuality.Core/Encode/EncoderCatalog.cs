using VideoQuality.Core.Tools;

namespace VideoQuality.Core.Encode;

/// <summary>How an encoder family spells quality, bitrate and two-pass on the ffmpeg command line.</summary>
public enum EncoderFamily
{
    Copy,
    X264,
    X265,
    SvtAv1,
    Nvenc,
    Qsv,
    Amf,
}

/// <summary>What the Encode page offers for one video encoder.</summary>
public sealed record VideoEncoderInfo(
    string Name,
    string Codec,
    EncoderFamily Family,
    IReadOnlyList<RateControl> Modes,
    double QualityMin,
    double QualityMax,
    double QualityDefault,
    string PresetOption,
    IReadOnlyList<string> Presets,
    string? DefaultPreset,
    string TuneOption,
    IReadOnlyList<string> Tunes,
    IReadOnlyList<string> Profiles,
    IReadOnlyList<int> BitDepths,
    string? ParamsOption);

/// <summary>What the Encode page offers for one audio encoder.</summary>
public sealed record AudioEncoderInfo(string Name, string Codec, bool HasBitrate, int DefaultBitrateKbps, IReadOnlyList<int> SampleRates);

/// <summary>
/// The encoders the Encode page knows how to drive. Presets, tunes and profiles follow ffmpeg's
/// documentation; the selection and grouping were adapted from FFmpegFreeUI's encoder database
/// (MIT, https://github.com/Lake1059/FFmpegFreeUI).
/// </summary>
public static class EncoderCatalog
{
    private static readonly string[] X26xPresets = ["placebo", "veryslow", "slower", "slow", "medium", "fast", "faster", "veryfast", "superfast", "ultrafast"];
    private static readonly string[] NvencPresets = ["p7", "p6", "p5", "p4", "p3", "p2", "p1"];
    private static readonly string[] QsvPresets = ["veryslow", "slower", "slow", "medium", "fast", "faster", "veryfast"];
    private static readonly string[] AmfQuality = ["quality", "balanced", "speed"];
    private static readonly RateControl[] SoftwareModes = [RateControl.Quality, RateControl.Qp, RateControl.Bitrate, RateControl.TwoPass, RateControl.TargetSize];
    private static readonly RateControl[] HardwareModes = [RateControl.Quality, RateControl.Qp, RateControl.Bitrate, RateControl.TwoPass, RateControl.TargetSize];
    private static readonly int[] Depths8And10 = [8, 10];

    public static IReadOnlyList<VideoEncoderInfo> Video { get; } =
    [
        new("libx265", "HEVC", EncoderFamily.X265, SoftwareModes, 0, 51, 20, "-preset", X26xPresets, "slow",
            "-tune", ["animation", "grain", "psnr", "ssim", "fastdecode", "zerolatency"],
            ["main", "main10", "main12"], [8, 10, 12], "-x265-params"),
        new("libx264", "H.264", EncoderFamily.X264, SoftwareModes, 0, 51, 18, "-preset", X26xPresets, "slower",
            "-tune", ["film", "animation", "grain", "stillimage", "psnr", "ssim", "fastdecode", "zerolatency"],
            ["baseline", "main", "high", "high10", "high422", "high444"], Depths8And10, "-x264-params"),
        new("libsvtav1", "AV1", EncoderFamily.SvtAv1, [RateControl.Quality, RateControl.Qp, RateControl.Bitrate], 0, 63, 30, "-preset",
            ["0", "1", "2", "3", "4", "5", "6", "7", "8", "9", "10", "11", "12", "13"], "6",
            "-tune", [], ["main"], Depths8And10, "-svtav1-params"),
        new("hevc_nvenc", "HEVC", EncoderFamily.Nvenc, HardwareModes, 0, 51, 26, "-preset", NvencPresets, "p7",
            "-tune", ["hq", "uhq", "ll", "ull", "lossless"], ["main", "main10", "rext"], Depths8And10, null),
        new("h264_nvenc", "H.264", EncoderFamily.Nvenc, HardwareModes, 0, 51, 23, "-preset", NvencPresets, "p7",
            "-tune", ["hq", "ll", "ull", "lossless"], ["baseline", "main", "high", "high444p"], [8], null),
        new("av1_nvenc", "AV1", EncoderFamily.Nvenc, HardwareModes, 0, 63, 36, "-preset", NvencPresets, "p7",
            "-tune", ["hq", "uhq", "ll", "ull", "lossless"], [], Depths8And10, null),
        new("hevc_qsv", "HEVC", EncoderFamily.Qsv, [RateControl.Quality, RateControl.Bitrate], 1, 51, 24, "-preset", QsvPresets, "veryslow",
            "-tune", [], ["main", "main10"], Depths8And10, null),
        new("h264_qsv", "H.264", EncoderFamily.Qsv, [RateControl.Quality, RateControl.Bitrate], 1, 51, 23, "-preset", QsvPresets, "veryslow",
            "-tune", [], ["baseline", "main", "high"], [8], null),
        new("av1_qsv", "AV1", EncoderFamily.Qsv, [RateControl.Quality, RateControl.Bitrate], 1, 255, 100, "-preset", QsvPresets, "veryslow",
            "-tune", [], ["main"], Depths8And10, null),
        new("hevc_amf", "HEVC", EncoderFamily.Amf, [RateControl.Qp, RateControl.Bitrate], 0, 51, 22, "-quality", AmfQuality, "quality",
            "-usage", ["transcoding", "high_quality", "lowlatency"], ["main", "main10"], Depths8And10, null),
        new("h264_amf", "H.264", EncoderFamily.Amf, [RateControl.Qp, RateControl.Bitrate], 0, 51, 22, "-quality", AmfQuality, "quality",
            "-usage", ["transcoding", "high_quality", "lowlatency"], ["main", "high"], [8], null),
        new("av1_amf", "AV1", EncoderFamily.Amf, [RateControl.Qp, RateControl.Bitrate], 0, 255, 100, "-quality", ["high_quality", .. AmfQuality], "quality",
            "-usage", ["transcoding", "high_quality", "lowlatency"], ["main"], Depths8And10, null),
        new("copy", "copy", EncoderFamily.Copy, [RateControl.Copy], 0, 0, 0, string.Empty, [], null, string.Empty, [], [], [], null),
    ];

    public static IReadOnlyList<AudioEncoderInfo> Audio { get; } =
    [
        new("aac", "aac", true, 320, [48000, 44100]),
        new("libopus", "opus", true, 192, [48000]),
        new("flac", "flac", false, 0, [48000, 44100, 96000]),
        new("libmp3lame", "mp3", true, 320, [48000, 44100]),
        new("ac3", "ac3", true, 448, [48000]),
        new("copy", "copy", false, 0, []),
        new("none", "none", false, 0, []),
    ];

    public static VideoEncoderInfo? FindVideo(string? name) =>
        Video.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));

    public static AudioEncoderInfo? FindAudio(string? name) =>
        Audio.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The video encoders this ffmpeg actually has (copy is always available).</summary>
    public static IReadOnlyList<VideoEncoderInfo> AvailableVideo(EncoderTool? tool) =>
        Video.Where(e => e.Family == EncoderFamily.Copy || tool?.HasEncoder(e.Name) == true).ToList();

    /// <summary>The audio encoders this ffmpeg actually has (copy and none are always available).</summary>
    public static IReadOnlyList<AudioEncoderInfo> AvailableAudio(EncoderTool? tool) =>
        Audio.Where(e => e.Name is "copy" or "none" || tool?.HasEncoder(e.Name) == true).ToList();
}
