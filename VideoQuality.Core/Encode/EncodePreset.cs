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
    public string Encoder { get; set; } = "libx265";

    public RateControl Mode { get; set; } = RateControl.Quality;

    /// <summary>CRF / CQ / ICQ value or QP, depending on <see cref="Mode"/>.</summary>
    public double Quality { get; set; } = 20;

    /// <summary>Video bitrate for <see cref="RateControl.Bitrate"/> and <see cref="RateControl.TwoPass"/>.</summary>
    public int BitrateKbps { get; set; } = 2000;

    /// <summary>Whole-file size per 24 minutes for <see cref="RateControl.TargetSize"/> (audio included).</summary>
    public int TargetMbPer24Minutes { get; set; } = 500;

    public string? Preset { get; set; }

    public string? Tune { get; set; }

    public string? Profile { get; set; }

    /// <summary>8, 10 or 12; 0 keeps the source's depth. Ignored when <see cref="PixelFormat"/> is set.</summary>
    public int BitDepth { get; set; }

    /// <summary>An explicit <c>-pix_fmt</c>, overriding <see cref="BitDepth"/>.</summary>
    public string? PixelFormat { get; set; }

    /// <summary>Keyframe interval in frames (<c>-g</c>); 0 is the encoder default.</summary>
    public int KeyframeInterval { get; set; }

    /// <summary>Force constant frame rate (<c>-fps_mode cfr</c>).</summary>
    public bool ConstantFrameRate { get; set; } = true;

    /// <summary>Output frame rate, e.g. <c>24000/1001</c>; empty keeps the source's.</summary>
    public string? FrameRate { get; set; }

    /// <summary>Encoder-native parameters (<c>-x265-params</c>, <c>-x264-params</c>, <c>-svtav1-params</c>).</summary>
    public string? EncoderParams { get; set; }
}

/// <summary>Picture filters, applied in a fixed order (see <see cref="FfmpegCommandBuilder"/>).</summary>
public sealed record FrameSettings
{
    /// <summary>Output width; 0 keeps the source, -2 follows the height keeping the aspect ratio.</summary>
    public int Width { get; set; }

    public int Height { get; set; }

    public string ScaleAlgorithm { get; set; } = "lanczos";

    public int CropLeft { get; set; }

    public int CropTop { get; set; }

    public int CropRight { get; set; }

    public int CropBottom { get; set; }

    public Deinterlace Deinterlace { get; set; }

    public Denoise Denoise { get; set; }

    /// <summary>Denoise strength, 1 (light) to 10 (strong).</summary>
    public int DenoiseStrength { get; set; } = 3;

    public bool Deband { get; set; }

    /// <summary>Unsharp amount (0 = off, 0.3 subtle … 1.5 strong).</summary>
    public double Sharpen { get; set; }

    public Flip Flip { get; set; }

    /// <summary>Clockwise rotation: 0, 90, 180 or 270.</summary>
    public int Rotate { get; set; }
}

/// <summary>Colour signalling written into the output (no conversion).</summary>
public sealed record ColorSettings
{
    public string? Primaries { get; set; }

    public string? Transfer { get; set; }

    public string? Matrix { get; set; }

    /// <summary><c>tv</c> (limited) or <c>pc</c> (full).</summary>
    public string? Range { get; set; }
}

/// <summary>Subtitles burned into the picture (内嵌) with libass.</summary>
public sealed record SubtitleBurn
{
    /// <summary>
    /// An .ass/.ssa/.srt file. Empty uses the subtitle stream <see cref="StreamIndex"/> of the input,
    /// or, when that is null too, a script beside the input with the same name.
    /// </summary>
    public string? File { get; set; }

    public int? StreamIndex { get; set; }

    /// <summary>Extra fonts for libass (the scripts' fonts, subset or not).</summary>
    public string? FontsDirectory { get; set; }

    /// <summary>ASS style overrides, e.g. <c>Fontsize=24</c> (mostly for SRT).</summary>
    public string? ForceStyle { get; set; }

    /// <summary>Text encoding of an SRT file (ASS is read as UTF-8).</summary>
    public string? CharacterEncoding { get; set; }
}

/// <summary>Audio: re-encode, copy or drop.</summary>
public sealed record AudioSettings
{
    /// <summary>ffmpeg encoder (<c>aac</c>, <c>libopus</c>, <c>flac</c>, …), <c>copy</c> or <c>none</c>.</summary>
    public string Encoder { get; set; } = "aac";

    public int BitrateKbps { get; set; } = 320;

    /// <summary>0 keeps the source's sample rate.</summary>
    public int SampleRate { get; set; } = 48000;

    /// <summary>0 keeps the source's channel count.</summary>
    public int Channels { get; set; }

    /// <summary>Lossless sample depth: 24 writes 24-bit FLAC; 0 keeps the encoder default.</summary>
    public int SampleBits { get; set; }

    /// <summary>Copy a track that is already this codec at or below this bitrate (no generation loss).</summary>
    public bool CopyWhenCompatible { get; set; } = true;

    /// <summary>ISO 639-2 language written on every output audio track (<c>jpn</c>); empty keeps the source's.</summary>
    public string? Language { get; set; }

    /// <summary>Only the first audio track (the usual Japanese track) instead of all of them.</summary>
    public bool FirstTrackOnly { get; set; }
}

/// <summary>Which streams and metadata go to the output, and an optional time range.</summary>
public sealed record StreamSettings
{
    /// <summary>Copy the input's subtitle tracks (not with MP4, which cannot hold ASS).</summary>
    public bool KeepSubtitles { get; set; }

    public bool KeepChapters { get; set; } = true;

    /// <summary>Copy the container's global tags (title, encoder, …); stream and chapter tags are always kept.</summary>
    public bool KeepMetadata { get; set; } = true;

    /// <summary>Copy attachments (fonts) — Matroska only.</summary>
    public bool KeepAttachments { get; set; }

    /// <summary>Start of the range to encode, in seconds.</summary>
    public double? Start { get; set; }

    /// <summary>End of the range to encode, in seconds.</summary>
    public double? End { get; set; }

    /// <summary>Chapters from an ffmetadata file (see ChapterList.ToFfMetadata) instead of the input's.</summary>
    public string? ChaptersFile { get; set; }

    /// <summary>Container title; empty keeps the source's.</summary>
    public string? Title { get; set; }
}

/// <summary>Free-form arguments for what the panels do not cover.</summary>
public sealed record CustomArguments
{
    /// <summary>Before <c>-i</c> (input options such as <c>-hwaccel cuda</c>).</summary>
    public string? Input { get; set; }

    public string? Video { get; set; }

    public string? Audio { get; set; }

    /// <summary>Appended to the video filter chain.</summary>
    public string? Filter { get; set; }

    /// <summary>Last options before the output file.</summary>
    public string? Output { get; set; }
}

/// <summary>
/// Everything one encode needs, saved as JSON so presets can be shared. Defaults follow common
/// fansub practice: x265 10-bit CRF 20, constant frame rate, AAC 320k at 48 kHz.
/// </summary>
public sealed record EncodePreset
{
    public string Name { get; set; } = "Custom";

    public string? Description { get; set; }

    /// <summary><c>mkv</c>, <c>mp4</c>, <c>mov</c> or <c>webm</c>.</summary>
    public string Container { get; set; } = "mkv";

    /// <summary>Output file name pattern; <c>{name}</c> is the input's name without extension.</summary>
    public string OutputPattern { get; set; } = "{name}.encoded";

    public VideoSettings Video { get; set; } = new();

    public FrameSettings Frame { get; set; } = new();

    public ColorSettings Color { get; set; } = new();

    public SubtitleBurn? Burn { get; set; }

    public AudioSettings Audio { get; set; } = new();

    public StreamSettings Streams { get; set; } = new();

    public CustomArguments Custom { get; set; } = new();

    /// <summary>Shipped with the app (read-only in the editor; "save as" makes a copy).</summary>
    [JsonIgnore]
    public bool BuiltIn { get; set; }
}
