using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using VideoQuality.Core.Chapters;
using VideoQuality.Core.Encode;
using VideoQuality.Core.Tools;

namespace VideoQuality.App.ViewModels;

/// <summary>
/// An editable copy of an <see cref="EncodePreset"/>: one bindable property per panel field.
/// <see cref="Changed"/> fires on every edit so the command preview stays live.
/// </summary>
public partial class PresetEditor : ObservableObject
{
    private bool _loading;

    public PresetEditor() => Load(new EncodePreset());

    public event EventHandler? Changed;

    public IReadOnlyList<string> Containers { get; } = ["mkv", "mp4", "mov", "webm"];

    /// <summary>Updated in place (never replaced) so a combo box keeps its selection.</summary>
    public ObservableCollection<VideoEncoderInfo> VideoEncoders { get; } = [.. EncoderCatalog.Video];

    public ObservableCollection<AudioEncoderInfo> AudioEncoders { get; } = [.. EncoderCatalog.Audio];

    public IReadOnlyList<int> SampleRates { get; } = [0, 44100, 48000, 96000];

    public IReadOnlyList<int> ChannelCounts { get; } = [0, 1, 2, 6, 8];

    public IReadOnlyList<int> SampleBitOptions { get; } = [0, 16, 24];

    public IReadOnlyList<Deinterlace> DeinterlaceOptions { get; } = Enum.GetValues<Deinterlace>();

    public IReadOnlyList<Denoise> DenoiseOptions { get; } = Enum.GetValues<Denoise>();

    public IReadOnlyList<Flip> FlipOptions { get; } = Enum.GetValues<Flip>();

    public IReadOnlyList<int> Rotations { get; } = [0, 90, 180, 270];

    public IReadOnlyList<string> ScaleAlgorithms { get; } = ["lanczos", "spline", "bicubic", "bilinear", "neighbor"];

    public IReadOnlyList<string> ColorPrimariesOptions { get; } = ["", "bt709", "bt2020", "smpte170m", "bt470bg"];

    public IReadOnlyList<string> ColorTransferOptions { get; } = ["", "bt709", "smpte2084", "arib-std-b67", "smpte170m"];

    public IReadOnlyList<string> ColorMatrixOptions { get; } = ["", "bt709", "bt2020nc", "smpte170m", "bt470bg"];

    public IReadOnlyList<string> ColorRangeOptions { get; } = ["", "tv", "pc"];

    // Output
    [ObservableProperty] public partial string Name { get; set; } = string.Empty;
    [ObservableProperty] public partial string? Description { get; set; }
    [ObservableProperty] public partial string Container { get; set; } = "mkv";
    [ObservableProperty] public partial string OutputPattern { get; set; } = "{name}.encoded";
    [ObservableProperty] public partial bool IsBuiltIn { get; set; }

    // Video
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Modes), nameof(EncoderPresets), nameof(Tunes), nameof(Profiles), nameof(BitDepths), nameof(IsCopy), nameof(HasParams))]
    public partial VideoEncoderInfo Encoder { get; set; } = EncoderCatalog.Video[0];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UsesQuality), nameof(UsesBitrate), nameof(UsesTargetSize))]
    public partial RateControl Mode { get; set; }

    [ObservableProperty] public partial decimal Quality { get; set; }
    [ObservableProperty] public partial decimal BitrateKbps { get; set; }
    [ObservableProperty] public partial decimal TargetMbPer24Minutes { get; set; }
    [ObservableProperty] public partial string? EncoderPreset { get; set; }
    [ObservableProperty] public partial string? Tune { get; set; }
    [ObservableProperty] public partial string? Profile { get; set; }
    [ObservableProperty] public partial int BitDepth { get; set; }
    [ObservableProperty] public partial string? PixelFormat { get; set; }
    [ObservableProperty] public partial decimal KeyframeInterval { get; set; }
    [ObservableProperty] public partial bool ConstantFrameRate { get; set; }
    [ObservableProperty] public partial string? FrameRate { get; set; }
    [ObservableProperty] public partial string? EncoderParams { get; set; }

    // Frame
    [ObservableProperty] public partial decimal Width { get; set; }
    [ObservableProperty] public partial decimal Height { get; set; }
    [ObservableProperty] public partial string ScaleAlgorithm { get; set; } = "lanczos";
    [ObservableProperty] public partial decimal CropLeft { get; set; }
    [ObservableProperty] public partial decimal CropTop { get; set; }
    [ObservableProperty] public partial decimal CropRight { get; set; }
    [ObservableProperty] public partial decimal CropBottom { get; set; }
    [ObservableProperty] public partial Deinterlace Deinterlace { get; set; }
    [ObservableProperty] public partial Denoise Denoise { get; set; }
    [ObservableProperty] public partial decimal DenoiseStrength { get; set; }
    [ObservableProperty] public partial bool Deband { get; set; }
    [ObservableProperty] public partial decimal Sharpen { get; set; }
    [ObservableProperty] public partial Flip Flip { get; set; }
    [ObservableProperty] public partial int Rotate { get; set; }

    // Colour
    [ObservableProperty] public partial string? Primaries { get; set; }
    [ObservableProperty] public partial string? Transfer { get; set; }
    [ObservableProperty] public partial string? Matrix { get; set; }
    [ObservableProperty] public partial string? Range { get; set; }

    // Burn-in
    [ObservableProperty] public partial bool BurnEnabled { get; set; }
    [ObservableProperty] public partial string? BurnFile { get; set; }
    [ObservableProperty] public partial decimal? BurnStream { get; set; }
    [ObservableProperty] public partial string? BurnFonts { get; set; }
    [ObservableProperty] public partial string? ForceStyle { get; set; }
    [ObservableProperty] public partial string? CharacterEncoding { get; set; }

    // Audio
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AudioHasBitrate))]
    public partial AudioEncoderInfo AudioEncoder { get; set; } = EncoderCatalog.Audio[0];

    [ObservableProperty] public partial decimal AudioBitrateKbps { get; set; }
    [ObservableProperty] public partial int SampleRate { get; set; }
    [ObservableProperty] public partial int Channels { get; set; }
    [ObservableProperty] public partial int SampleBits { get; set; }
    [ObservableProperty] public partial bool CopyWhenCompatible { get; set; }
    [ObservableProperty] public partial bool FirstTrackOnly { get; set; }

    // Streams
    [ObservableProperty] public partial bool KeepSubtitles { get; set; }
    [ObservableProperty] public partial bool KeepChapters { get; set; }
    [ObservableProperty] public partial bool KeepMetadata { get; set; }
    [ObservableProperty] public partial bool KeepAttachments { get; set; }
    [ObservableProperty] public partial string? Start { get; set; }
    [ObservableProperty] public partial string? End { get; set; }
    [ObservableProperty] public partial string? Title { get; set; }

    // Custom
    [ObservableProperty] public partial string? CustomInput { get; set; }
    [ObservableProperty] public partial string? CustomVideo { get; set; }
    [ObservableProperty] public partial string? CustomAudio { get; set; }
    [ObservableProperty] public partial string? CustomFilter { get; set; }
    [ObservableProperty] public partial string? CustomOutput { get; set; }

    public IReadOnlyList<RateControl> Modes => Encoder.Modes;

    public IReadOnlyList<string> EncoderPresets => Encoder.Presets;

    public IReadOnlyList<string> Tunes => ["", .. Encoder.Tunes];

    public IReadOnlyList<string> Profiles => ["", .. Encoder.Profiles];

    public IReadOnlyList<int> BitDepths => [0, .. Encoder.BitDepths];

    public bool IsCopy => Encoder.Family == EncoderFamily.Copy;

    public bool HasParams => Encoder.ParamsOption is not null;

    public bool UsesQuality => Mode is RateControl.Quality or RateControl.Qp;

    public bool UsesBitrate => Mode is RateControl.Bitrate or RateControl.TwoPass;

    public bool UsesTargetSize => Mode == RateControl.TargetSize;

    public bool AudioHasBitrate => AudioEncoder.HasBitrate;

    /// <summary>Limits the encoder lists to what the located ffmpeg has (all when none is known).</summary>
    public void SetAvailable(EncoderTool? tool)
    {
        // The selected encoder stays listed even when this ffmpeg lacks it (the preview then warns).
        Sync(VideoEncoders, tool is null ? EncoderCatalog.Video : EncoderCatalog.AvailableVideo(tool), Encoder, EncoderCatalog.Video);
        Sync(AudioEncoders, tool is null ? EncoderCatalog.Audio : EncoderCatalog.AvailableAudio(tool), AudioEncoder, EncoderCatalog.Audio);
    }

    /// <summary>Makes <paramref name="target"/> hold <paramref name="wanted"/> (plus the selection), in catalog order.</summary>
    internal static void Sync<T>(ObservableCollection<T> target, IReadOnlyList<T> wanted, T selected, IReadOnlyList<T> order)
    {
        var keep = new HashSet<T>(wanted) { selected };
        List<T> desired = [.. order.Where(keep.Contains)];
        foreach (T item in target.Where(i => !keep.Contains(i)).ToList())
        {
            target.Remove(item);
        }

        // What is left is in catalog order, so inserting at the first mismatch rebuilds the list.
        for (int i = 0; i < desired.Count; i++)
        {
            if (i >= target.Count || !EqualityComparer<T>.Default.Equals(target[i], desired[i]))
            {
                target.Insert(i, desired[i]);
            }
        }
    }

    public void Load(EncodePreset preset)
    {
        _loading = true;
        Name = preset.Name;
        Description = preset.Description;
        Container = preset.Container;
        OutputPattern = preset.OutputPattern;
        IsBuiltIn = preset.BuiltIn;

        VideoSettings v = preset.Video;
        Encoder = EncoderCatalog.FindVideo(v.Encoder) ?? EncoderCatalog.Video[0];
        Mode = Encoder.Modes.Contains(v.Mode) ? v.Mode : Encoder.Modes[0];
        Quality = (decimal)v.Quality;
        BitrateKbps = v.BitrateKbps;
        TargetMbPer24Minutes = v.TargetMbPer24Minutes;
        EncoderPreset = v.Preset ?? Encoder.DefaultPreset;
        Tune = v.Tune ?? string.Empty;
        Profile = v.Profile ?? string.Empty;
        BitDepth = v.BitDepth;
        PixelFormat = v.PixelFormat;
        KeyframeInterval = v.KeyframeInterval;
        ConstantFrameRate = v.ConstantFrameRate;
        FrameRate = v.FrameRate;
        EncoderParams = v.EncoderParams;

        FrameSettings f = preset.Frame;
        Width = f.Width;
        Height = f.Height;
        ScaleAlgorithm = f.ScaleAlgorithm;
        CropLeft = f.CropLeft;
        CropTop = f.CropTop;
        CropRight = f.CropRight;
        CropBottom = f.CropBottom;
        Deinterlace = f.Deinterlace;
        Denoise = f.Denoise;
        DenoiseStrength = f.DenoiseStrength;
        Deband = f.Deband;
        Sharpen = (decimal)f.Sharpen;
        Flip = f.Flip;
        Rotate = f.Rotate;

        Primaries = preset.Color.Primaries ?? string.Empty;
        Transfer = preset.Color.Transfer ?? string.Empty;
        Matrix = preset.Color.Matrix ?? string.Empty;
        Range = preset.Color.Range ?? string.Empty;

        BurnEnabled = preset.Burn is not null;
        BurnFile = preset.Burn?.File;
        BurnStream = preset.Burn?.StreamIndex;
        BurnFonts = preset.Burn?.FontsDirectory;
        ForceStyle = preset.Burn?.ForceStyle;
        CharacterEncoding = preset.Burn?.CharacterEncoding;

        AudioSettings a = preset.Audio;
        AudioEncoder = EncoderCatalog.FindAudio(a.Encoder) ?? EncoderCatalog.Audio[0];
        AudioBitrateKbps = a.BitrateKbps;
        SampleRate = a.SampleRate;
        Channels = a.Channels;
        SampleBits = a.SampleBits;
        CopyWhenCompatible = a.CopyWhenCompatible;
        FirstTrackOnly = a.FirstTrackOnly;

        StreamSettings s = preset.Streams;
        KeepSubtitles = s.KeepSubtitles;
        KeepChapters = s.KeepChapters;
        KeepMetadata = s.KeepMetadata;
        KeepAttachments = s.KeepAttachments;
        Start = s.Start is double start ? FormatTime(start) : null;
        End = s.End is double end ? FormatTime(end) : null;
        Title = s.Title;

        CustomInput = preset.Custom.Input;
        CustomVideo = preset.Custom.Video;
        CustomAudio = preset.Custom.Audio;
        CustomFilter = preset.Custom.Filter;
        CustomOutput = preset.Custom.Output;
        _loading = false;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public EncodePreset ToPreset() => new()
    {
        Name = Name,
        Description = Description,
        Container = Container,
        OutputPattern = string.IsNullOrWhiteSpace(OutputPattern) ? "{name}.encoded" : OutputPattern,
        Video = new VideoSettings
        {
            Encoder = Encoder.Name,
            Mode = IsCopy ? RateControl.Copy : Mode,
            Quality = (double)Quality,
            BitrateKbps = (int)BitrateKbps,
            TargetMbPer24Minutes = (int)TargetMbPer24Minutes,
            Preset = Blank(EncoderPreset),
            Tune = Blank(Tune),
            Profile = Blank(Profile),
            BitDepth = BitDepth,
            PixelFormat = Blank(PixelFormat),
            KeyframeInterval = (int)KeyframeInterval,
            ConstantFrameRate = ConstantFrameRate,
            FrameRate = Blank(FrameRate),
            EncoderParams = Blank(EncoderParams),
        },
        Frame = new FrameSettings
        {
            Width = (int)Width,
            Height = (int)Height,
            ScaleAlgorithm = ScaleAlgorithm,
            CropLeft = (int)CropLeft,
            CropTop = (int)CropTop,
            CropRight = (int)CropRight,
            CropBottom = (int)CropBottom,
            Deinterlace = Deinterlace,
            Denoise = Denoise,
            DenoiseStrength = (int)DenoiseStrength,
            Deband = Deband,
            Sharpen = (double)Sharpen,
            Flip = Flip,
            Rotate = Rotate,
        },
        Color = new ColorSettings { Primaries = Blank(Primaries), Transfer = Blank(Transfer), Matrix = Blank(Matrix), Range = Blank(Range) },
        Burn = BurnEnabled
            ? new SubtitleBurn
            {
                File = Blank(BurnFile),
                StreamIndex = BurnStream is decimal stream ? (int)stream : null,
                FontsDirectory = Blank(BurnFonts),
                ForceStyle = Blank(ForceStyle),
                CharacterEncoding = Blank(CharacterEncoding),
            }
            : null,
        Audio = new AudioSettings
        {
            Encoder = AudioEncoder.Name,
            BitrateKbps = (int)AudioBitrateKbps,
            SampleRate = SampleRate,
            Channels = Channels,
            SampleBits = SampleBits,
            CopyWhenCompatible = CopyWhenCompatible,
            FirstTrackOnly = FirstTrackOnly,
        },
        Streams = new StreamSettings
        {
            KeepSubtitles = KeepSubtitles,
            KeepChapters = KeepChapters,
            KeepMetadata = KeepMetadata,
            KeepAttachments = KeepAttachments,
            Start = ParseTime(Start),
            End = ParseTime(End),
            Title = Blank(Title),
        },
        Custom = new CustomArguments
        {
            Input = Blank(CustomInput),
            Video = Blank(CustomVideo),
            Audio = Blank(CustomAudio),
            Filter = Blank(CustomFilter),
            Output = Blank(CustomOutput),
        },
    };

    /// <summary>Seconds (<c>90.5</c>) or a clock time (<c>1:30</c>, <c>00:01:30.500</c>); null when empty or invalid.</summary>
    public static double? ParseTime(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string trimmed = text.Trim();
        if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds))
        {
            return seconds;
        }

        string padded = trimmed.Count(c => c == ':') == 1 ? "0:" + trimmed : trimmed;
        try
        {
            return ChapterList.ParseTime(padded).TotalSeconds;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public static string FormatTime(double seconds) => ChapterList.FormatTime(TimeSpan.FromSeconds(seconds));

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_loading)
        {
            return;
        }

        if (e.PropertyName == nameof(Encoder))
        {
            // A new encoder: its own default preset and a mode it supports.
            _loading = true;
            EncoderPreset = Encoder.DefaultPreset;
            Tune = string.Empty;
            Profile = string.Empty;
            if (!Encoder.Modes.Contains(Mode))
            {
                Mode = Encoder.Modes[0];
            }

            if (Mode is RateControl.Quality)
            {
                Quality = (decimal)Encoder.QualityDefault;
            }

            if (BitDepth != 0 && !Encoder.BitDepths.Contains(BitDepth))
            {
                BitDepth = 0;
            }

            _loading = false;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
