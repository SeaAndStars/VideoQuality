using System.Globalization;
using System.Text;
using FFmpeg.AutoGen;
using VideoQuality.Core.Decoding;
using VideoQuality.Core.Localization;

namespace VideoQuality.Core.Media;

public enum StreamKind
{
    Video,
    Audio,
    Subtitle,
    Attachment,
    Data,
}

/// <summary>One stream of a media file.</summary>
public sealed record MediaStream
{
    public int Index { get; init; }

    public StreamKind Kind { get; init; }

    /// <summary>ffmpeg codec name (<c>hevc</c>, <c>aac</c>, <c>ass</c>, …).</summary>
    public string Codec { get; init; } = string.Empty;

    public string Profile { get; init; } = string.Empty;

    public long BitRate { get; init; }

    public string Language { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public bool Default { get; init; }

    public bool Forced { get; init; }

    // Video
    public int Width { get; init; }

    public int Height { get; init; }

    public string PixelFormat { get; init; } = string.Empty;

    public int BitDepth { get; init; }

    public double FrameRate { get; init; }

    /// <summary>The frame rate as a rational, e.g. <c>24000/1001</c>.</summary>
    public string FrameRateText { get; init; } = string.Empty;

    public string ColorPrimaries { get; init; } = string.Empty;

    public string ColorTransfer { get; init; } = string.Empty;

    public string ColorMatrix { get; init; } = string.Empty;

    public string ColorRange { get; init; } = string.Empty;

    // Audio
    public int SampleRate { get; init; }

    public int Channels { get; init; }

    public string ChannelLayout { get; init; } = string.Empty;

    /// <summary>Bits per raw sample (FLAC/PCM), 0 when not meaningful.</summary>
    public int SampleBits { get; init; }

    // Attachment
    public string FileName { get; init; } = string.Empty;

    public string MimeType { get; init; } = string.Empty;
}

public sealed record MediaChapter(TimeSpan Start, TimeSpan End, string Title);

/// <summary>What a media file contains, read in-process with libav (no ffprobe needed).</summary>
public sealed record MediaInfo
{
    public string Path { get; init; } = string.Empty;

    /// <summary>Container name as libav reports it (<c>matroska,webm</c>, <c>mov,mp4,m4a,3gp,3g2,mj2</c>).</summary>
    public string Format { get; init; } = string.Empty;

    public string FormatLongName { get; init; } = string.Empty;

    public TimeSpan Duration { get; init; }

    public long Size { get; init; }

    public long BitRate { get; init; }

    public string Title { get; init; } = string.Empty;

    public IReadOnlyList<MediaStream> Streams { get; init; } = [];

    public IReadOnlyList<MediaChapter> Chapters { get; init; } = [];

    public IReadOnlyDictionary<string, string> Tags { get; init; } = new Dictionary<string, string>();

    public MediaStream? Video => Streams.FirstOrDefault(s => s.Kind == StreamKind.Video);

    public IEnumerable<MediaStream> AudioStreams => Streams.Where(s => s.Kind == StreamKind.Audio);

    public IEnumerable<MediaStream> SubtitleStreams => Streams.Where(s => s.Kind == StreamKind.Subtitle);

    public IEnumerable<MediaStream> Attachments => Streams.Where(s => s.Kind == StreamKind.Attachment);

    public bool IsMatroska => Format.Contains("matroska", StringComparison.Ordinal);

    public bool IsMp4 => Format.Contains("mp4", StringComparison.Ordinal);

    /// <summary>A plain-text report like MediaInfo's / HandBrake's summary, for copying into chats.</summary>
    public string ToText()
    {
        var text = new StringBuilder();
        text.AppendLine(Path);
        text.AppendLine(Loc.T("media.text.format", FormatLongName.Length > 0 ? FormatLongName : Format, FormatClock(Duration), FormatSize(Size), FormatKbps(BitRate)));
        if (Title.Length > 0)
        {
            text.AppendLine(Loc.T("media.text.title", Title));
        }

        foreach (MediaStream stream in Streams)
        {
            text.Append("  #").Append(stream.Index).Append(' ').Append(Describe(stream));
            if (stream.Language.Length > 0)
            {
                text.Append(" [").Append(stream.Language).Append(']');
            }

            if (stream.Title.Length > 0)
            {
                text.Append(" \"").Append(stream.Title).Append('"');
            }

            if (stream.Default)
            {
                text.Append(' ').Append(Loc.T("track.flag.default"));
            }

            if (stream.Forced)
            {
                text.Append(' ').Append(Loc.T("track.flag.forced"));
            }

            text.AppendLine();
        }

        foreach (MediaChapter chapter in Chapters)
        {
            text.Append("  ").Append(FormatClock(chapter.Start)).Append(' ').AppendLine(chapter.Title);
        }

        return text.ToString();
    }

    /// <summary>One-line description of a stream, e.g. <c>hevc Main 10 1920×1080 23.976 fps yuv420p10le</c>.</summary>
    public static string Describe(MediaStream stream) => stream.Kind switch
    {
        StreamKind.Video => Join(stream.Codec, stream.Profile, $"{stream.Width}×{stream.Height}",
            stream.FrameRate > 0 ? stream.FrameRate.ToString("0.###", CultureInfo.InvariantCulture) + " fps" : string.Empty,
            stream.PixelFormat, stream.BitRate > 0 ? FormatKbps(stream.BitRate) : string.Empty),
        StreamKind.Audio => Join(stream.Codec, stream.Profile,
            stream.SampleRate > 0 ? (stream.SampleRate / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + " kHz" : string.Empty,
            stream.ChannelLayout.Length > 0 ? stream.ChannelLayout : stream.Channels > 0 ? $"{stream.Channels} ch" : string.Empty,
            stream.SampleBits > 0 ? $"{stream.SampleBits}-bit" : string.Empty,
            stream.BitRate > 0 ? FormatKbps(stream.BitRate) : string.Empty),
        StreamKind.Attachment => Join(stream.FileName, stream.MimeType),
        _ => stream.Codec,
    };

    public static string FormatClock(TimeSpan time) => time.ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture);

    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => (bytes / (double)(1L << 30)).ToString("0.00", CultureInfo.InvariantCulture) + " GiB",
        >= 1L << 20 => (bytes / (double)(1L << 20)).ToString("0.0", CultureInfo.InvariantCulture) + " MiB",
        _ => (bytes / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " KiB",
    };

    public static string FormatKbps(long bitsPerSecond) =>
        (bitsPerSecond / 1000.0).ToString("0", CultureInfo.InvariantCulture) + " kb/s";

    private static string Join(params string[] parts) => string.Join(' ', parts.Where(p => p.Length > 0));
}

/// <summary>Reads <see cref="MediaInfo"/> with the in-process (LGPL) libav libraries.</summary>
public static unsafe class MediaInfoReader
{
    public static MediaInfo Read(string path)
    {
        FfmpegRuntime.Initialize();
        AVFormatContext* format = null;
        try
        {
            FrameDecoder.Check(ffmpeg.avformat_open_input(&format, path, null, null), "probe.error.open", path);
            FrameDecoder.Check(ffmpeg.avformat_find_stream_info(format, null), "probe.error.streamInfo", path);

            var streams = new List<MediaStream>((int)format->nb_streams);
            for (int i = 0; i < format->nb_streams; i++)
            {
                streams.Add(ReadStream(format->streams[i]));
            }

            var chapters = new List<MediaChapter>((int)format->nb_chapters);
            for (int i = 0; i < format->nb_chapters; i++)
            {
                AVChapter* chapter = format->chapters[i];
                double unit = ffmpeg.av_q2d(chapter->time_base);
                chapters.Add(new MediaChapter(
                    TimeSpan.FromSeconds(chapter->start * unit),
                    TimeSpan.FromSeconds(chapter->end * unit),
                    Tag(chapter->metadata, "title")));
            }

            Dictionary<string, string> tags = Tags(format->metadata);
            return new MediaInfo
            {
                Path = path,
                Format = Text(format->iformat->name),
                FormatLongName = Text(format->iformat->long_name),
                Duration = format->duration > 0 ? TimeSpan.FromSeconds(format->duration / (double)ffmpeg.AV_TIME_BASE) : TimeSpan.Zero,
                Size = new FileInfo(path).Length,
                BitRate = format->bit_rate,
                Title = tags.GetValueOrDefault("title", string.Empty),
                Streams = streams,
                Chapters = chapters,
                Tags = tags,
            };
        }
        finally
        {
            if (format != null)
            {
                ffmpeg.avformat_close_input(&format);
            }
        }
    }

    private static MediaStream ReadStream(AVStream* stream)
    {
        AVCodecParameters* p = stream->codecpar;
        Dictionary<string, string> tags = Tags(stream->metadata);
        StreamKind kind = p->codec_type switch
        {
            AVMediaType.AVMEDIA_TYPE_VIDEO when (stream->disposition & ffmpeg.AV_DISPOSITION_ATTACHED_PIC) != 0 => StreamKind.Attachment,
            AVMediaType.AVMEDIA_TYPE_VIDEO => StreamKind.Video,
            AVMediaType.AVMEDIA_TYPE_AUDIO => StreamKind.Audio,
            AVMediaType.AVMEDIA_TYPE_SUBTITLE => StreamKind.Subtitle,
            AVMediaType.AVMEDIA_TYPE_ATTACHMENT => StreamKind.Attachment,
            _ => StreamKind.Data,
        };

        string pixelFormat = kind == StreamKind.Video ? Text(ffmpeg.av_get_pix_fmt_name((AVPixelFormat)p->format)) : string.Empty;
        AVRational rate = stream->avg_frame_rate.num != 0 && stream->avg_frame_rate.den != 0 ? stream->avg_frame_rate : stream->r_frame_rate;
        byte* layout = stackalloc byte[64];
        string channelLayout = string.Empty;
        if (kind == StreamKind.Audio && ffmpeg.av_channel_layout_describe(&p->ch_layout, layout, 64) > 0)
        {
            channelLayout = Text(layout);
        }

        return new MediaStream
        {
            Index = stream->index,
            Kind = kind,
            Codec = Text(ffmpeg.avcodec_get_name(p->codec_id)),
            Profile = Text(ffmpeg.avcodec_profile_name(p->codec_id, p->profile)),
            BitRate = p->bit_rate > 0 ? p->bit_rate : StatisticsBitRate(tags),
            Language = tags.GetValueOrDefault("language", string.Empty),
            Title = tags.GetValueOrDefault("title", string.Empty),
            Default = (stream->disposition & ffmpeg.AV_DISPOSITION_DEFAULT) != 0,
            Forced = (stream->disposition & ffmpeg.AV_DISPOSITION_FORCED) != 0,
            Width = p->width,
            Height = p->height,
            PixelFormat = pixelFormat,
            BitDepth = kind == StreamKind.Video ? FrameDecoder.ResolveBitDepth(p->bits_per_raw_sample, pixelFormat) : 0,
            FrameRate = kind == StreamKind.Video && rate.den != 0 ? ffmpeg.av_q2d(rate) : 0,
            FrameRateText = kind == StreamKind.Video && rate.den != 0 ? $"{rate.num}/{rate.den}" : string.Empty,
            ColorPrimaries = kind == StreamKind.Video ? Text(ffmpeg.av_color_primaries_name(p->color_primaries)) : string.Empty,
            ColorTransfer = kind == StreamKind.Video ? Text(ffmpeg.av_color_transfer_name(p->color_trc)) : string.Empty,
            ColorMatrix = kind == StreamKind.Video ? Text(ffmpeg.av_color_space_name(p->color_space)) : string.Empty,
            ColorRange = kind == StreamKind.Video ? Text(ffmpeg.av_color_range_name(p->color_range)) : string.Empty,
            SampleRate = p->sample_rate,
            Channels = p->ch_layout.nb_channels,
            ChannelLayout = channelLayout,
            SampleBits = kind == StreamKind.Audio ? p->bits_per_raw_sample : 0,
            FileName = tags.GetValueOrDefault("filename", string.Empty),
            MimeType = tags.GetValueOrDefault("mimetype", string.Empty),
        };
    }

    /// <summary>Matroska muxers write the real bitrate as a <c>BPS</c> statistics tag.</summary>
    internal static long StatisticsBitRate(IReadOnlyDictionary<string, string> tags) =>
        tags.TryGetValue("BPS", out string? value) && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long bps) ? bps : 0;

    private static Dictionary<string, string> Tags(AVDictionary* dictionary)
    {
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AVDictionaryEntry* entry = null;
        while ((entry = ffmpeg.av_dict_get(dictionary, string.Empty, entry, ffmpeg.AV_DICT_IGNORE_SUFFIX)) != null)
        {
            tags[Text(entry->key)] = Text(entry->value);
        }

        return tags;
    }

    private static string Tag(AVDictionary* dictionary, string key) => Tags(dictionary).GetValueOrDefault(key, string.Empty);

    // Interpolation turns a null native string into an empty one.
    private static string Text(byte* value) => $"{System.Runtime.InteropServices.Marshal.PtrToStringUTF8((IntPtr)value)}";

    private static string Text(string? value) => $"{value}";
}
