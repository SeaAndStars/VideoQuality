using System.Text.Json.Serialization;

namespace VideoQuality.Core.Encode;

/// <summary>How the video bitrate is decided.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RateControl>))]
public enum RateControl
{
    /// <summary>Constant quality (x264/x265/SVT-AV1 CRF, NVENC CQ, QSV ICQ).</summary>
    Quality,

    /// <summary>Fixed quantiser.</summary>
    Qp,

    /// <summary>Average bitrate, one pass.</summary>
    Bitrate,

    /// <summary>Average bitrate, two passes (x264/x265; NVENC uses its multipass mode).</summary>
    TwoPass,

    /// <summary>A file size per 24 minutes (the fansub convention), turned into a two-pass bitrate.</summary>
    TargetSize,

    /// <summary>No re-encode: copy the video stream.</summary>
    Copy,
}

[JsonConverter(typeof(JsonStringEnumConverter<Deinterlace>))]
public enum Deinterlace
{
    None,
    Bwdif,
    Yadif,
}

[JsonConverter(typeof(JsonStringEnumConverter<Denoise>))]
public enum Denoise
{
    None,
    Hqdn3d,
    Nlmeans,
}

[JsonConverter(typeof(JsonStringEnumConverter<Flip>))]
public enum Flip
{
    None,
    Horizontal,
    Vertical,
}

/// <summary>The video encoder and its rate control.</summary>
public sealed record VideoSettings
{
    /// <summary>ffmpeg encoder name (<c>libx265</c>, <c>hevc_nvenc</c>, …) or <c>copy</c>.</summary>
    public string Encoder { get; init; } = "libx265";

    public RateControl Mode { get; init; } = RateControl.Quality;

    /// <summary>CRF / CQ / ICQ value or QP, depending on <see cref="Mode"/>.</summary>
    public double Quality { get; init; } = 20;

    /// <summary>Video bitrate for <see cref="RateControl.Bitrate"/> and <see cref="RateControl.TwoPass"/>.</summary>
    public int BitrateKbps { get; init; } = 2000;

    /// <summary>Whole-file size per 24 minutes for <see cref="RateControl.TargetSize"/> (audio included).</summary>
    public int TargetMbPer24Minutes { get; init; } = 500;

    public string? Preset { get; init; }

    public string? Tune { get; init; }

    public string? Profile { get; init; }

    /// <summary>8, 10 or 12; 0 keeps the source's depth. Ignored when <see cref="PixelFormat"/> is set.</summary>
    public int BitDepth { get; init; }

    /// <summary>An explicit <c>-pix_fmt</c>, overriding <see cref="BitDepth"/>.</summary>
    public string? PixelFormat { get; init; }

    /// <summary>Keyframe interval in frames (<c>-g</c>); 0 is the encoder default.</summary>
    public int KeyframeInterval { get; init; }

    /// <summary>Force constant frame rate (<c>-fps_mode cfr</c>).</summary>
    public bool ConstantFrameRate { get; init; } = true;

    /// <summary>Output frame rate, e.g. <c>24000/1001</c>; empty keeps the source's.</summary>
    public string? FrameRate { get; init; }

    /// <summary>Encoder-native parameters (<c>-x265-params</c>, <c>-x264-params</c>, <c>-svtav1-params</c>).</summary>
    public string? EncoderParams { get; init; }
}

/// <summary>Picture filters, applied in a fixed order (see <see cref="FfmpegCommandBuilder"/>).</summary>
public sealed record FrameSettings
{
    /// <summary>Output width; 0 keeps the source, -2 follows the height keeping the aspect ratio.</summary>
    public int Width { get; init; }

    public int Height { get; init; }

    public string ScaleAlgorithm { get; init; } = "lanczos";

    public int CropLeft { get; init; }

    public int CropTop { get; init; }

    public int CropRight { get; init; }

    public int CropBottom { get; init; }

    public Deinterlace Deinterlace { get; init; }

    public Denoise Denoise { get; init; }

    /// <summary>Denoise strength, 1 (light) to 10 (strong).</summary>
    public int DenoiseStrength { get; init; } = 3;

    public bool Deband { get; init; }

    /// <summary>Unsharp amount (0 = off, 0.3 subtle … 1.5 strong).</summary>
    public double Sharpen { get; init; }

    public Flip Flip { get; init; }

    /// <summary>Clockwise rotation: 0, 90, 180 or 270.</summary>
    public int Rotate { get; init; }
}

/// <summary>Colour signalling written into the output (no conversion).</summary>
public sealed record ColorSettings
{
    public string? Primaries { get; init; }

    public string? Transfer { get; init; }

    public string? Matrix { get; init; }

    /// <summary><c>tv</c> (limited) or <c>pc</c> (full).</summary>
    public string? Range { get; init; }
}

/// <summary>Subtitles burned into the picture (内嵌) with libass.</summary>
public sealed record SubtitleBurn
{
    /// <summary>
    /// An .ass/.ssa/.srt file. Empty uses the subtitle stream <see cref="StreamIndex"/> of the input,
    /// or, when that is null too, a script beside the input with the same name.
    /// </summary>
    public string? File { get; init; }

    public int? StreamIndex { get; init; }

    /// <summary>Extra fonts for libass (the scripts' fonts, subset or not).</summary>
    public string? FontsDirectory { get; init; }

    /// <summary>ASS style overrides, e.g. <c>Fontsize=24</c> (mostly for SRT).</summary>
    public string? ForceStyle { get; init; }

    /// <summary>Text encoding of an SRT file (ASS is read as UTF-8).</summary>
    public string? CharacterEncoding { get; init; }
}

/// <summary>Audio: re-encode, copy or drop.</summary>
public sealed record AudioSettings
{
    /// <summary>ffmpeg encoder (<c>aac</c>, <c>libopus</c>, <c>flac</c>, …), <c>copy</c> or <c>none</c>.</summary>
    public string Encoder { get; init; } = "aac";

    public int BitrateKbps { get; init; } = 320;

    /// <summary>0 keeps the source's sample rate.</summary>
    public int SampleRate { get; init; } = 48000;

    /// <summary>0 keeps the source's channel count.</summary>
    public int Channels { get; init; }

    /// <summary>Lossless sample depth: 24 writes 24-bit FLAC; 0 keeps the encoder default.</summary>
    public int SampleBits { get; init; }

    /// <summary>Copy a track that is already this codec at or below this bitrate (no generation loss).</summary>
    public bool CopyWhenCompatible { get; init; } = true;

    /// <summary>ISO 639-2 language written on every output audio track (<c>jpn</c>); empty keeps the source's.</summary>
    public string? Language { get; init; }

    /// <summary>Only the first audio track (the usual Japanese track) instead of all of them.</summary>
    public bool FirstTrackOnly { get; init; }
}

/// <summary>Which streams and metadata go to the output, and an optional time range.</summary>
public sealed record StreamSettings
{
    /// <summary>Copy the input's subtitle tracks (not with MP4, which cannot hold ASS).</summary>
    public bool KeepSubtitles { get; init; }

    public bool KeepChapters { get; init; } = true;

    /// <summary>Copy the container's global tags (title, encoder, …); stream and chapter tags are always kept.</summary>
    public bool KeepMetadata { get; init; } = true;

    /// <summary>Copy attachments (fonts) — Matroska only.</summary>
    public bool KeepAttachments { get; init; }

    /// <summary>Start of the range to encode, in seconds.</summary>
    public double? Start { get; init; }

    /// <summary>End of the range to encode, in seconds.</summary>
    public double? End { get; init; }

    /// <summary>Chapters from an ffmetadata file (see ChapterList.ToFfMetadata) instead of the input's.</summary>
    public string? ChaptersFile { get; init; }

    /// <summary>Container title; empty keeps the source's.</summary>
    public string? Title { get; init; }
}

/// <summary>Free-form arguments for what the panels do not cover.</summary>
public sealed record CustomArguments
{
    /// <summary>Before <c>-i</c> (input options such as <c>-hwaccel cuda</c>).</summary>
    public string? Input { get; init; }

    public string? Video { get; init; }

    public string? Audio { get; init; }

    /// <summary>Appended to the video filter chain.</summary>
    public string? Filter { get; init; }

    /// <summary>Last options before the output file.</summary>
    public string? Output { get; init; }
}

/// <summary>
/// Everything one encode needs, saved as JSON so presets can be shared. Defaults follow common
/// fansub practice: x265 10-bit CRF 20, constant frame rate, AAC 320k at 48 kHz.
/// </summary>
public sealed record EncodePreset
{
    public string Name { get; init; } = "Custom";

    public string? Description { get; init; }

    /// <summary><c>mkv</c>, <c>mp4</c>, <c>mov</c> or <c>webm</c>.</summary>
    public string Container { get; init; } = "mkv";

    /// <summary>Output file name pattern; <c>{name}</c> is the input's name without extension.</summary>
    public string OutputPattern { get; init; } = "{name}.encoded";

    public VideoSettings Video { get; init; } = new();

    public FrameSettings Frame { get; init; } = new();

    public ColorSettings Color { get; init; } = new();

    public SubtitleBurn? Burn { get; init; }

    public AudioSettings Audio { get; init; } = new();

    public StreamSettings Streams { get; init; } = new();

    public CustomArguments Custom { get; init; } = new();

    /// <summary>Shipped with the app (read-only in the editor; "save as" makes a copy).</summary>
    [JsonIgnore]
    public bool BuiltIn { get; init; }
}
