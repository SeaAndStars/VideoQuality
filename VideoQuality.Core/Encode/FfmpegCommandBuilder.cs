using System.Globalization;
using System.Text;
using VideoQuality.Core.Localization;
using VideoQuality.Core.Media;

namespace VideoQuality.Core.Encode;

/// <summary>The ffmpeg runs one encode needs (two for two-pass), and what the user should know.</summary>
public sealed record EncodeCommand(
    IReadOnlyList<IReadOnlyList<string>> Passes,
    string Output,
    IReadOnlyList<string> TemporaryFiles,
    IReadOnlyList<string> Warnings,
    int VideoBitrateKbps,
    TimeSpan Duration,
    double FrameRate = 0);

/// <summary>
/// Turns an <see cref="EncodePreset"/> and a source into ffmpeg arguments. Pure: no files are read
/// except checking for a same-named subtitle script; every decision is unit-tested.
/// </summary>
/// <remarks>
/// Video filters run in a fixed order: crop → deinterlace → denoise → deband → scale → flip/rotate →
/// sharpen → burned-in subtitles (rendered at the output size) → the preset's custom filters.
/// </remarks>
public static class FfmpegCommandBuilder
{
    private static readonly string[] SubtitleExtensions = [".ass", ".ssa", ".srt"];

    /// <param name="source">The input file.</param>
    /// <param name="info">What the input contains (durations, audio codecs, sizes).</param>
    /// <param name="output">The output file.</param>
    /// <param name="workDirectory">Where two-pass statistics go.</param>
    public static EncodeCommand Build(EncodePreset preset, string source, MediaInfo info, string output, string workDirectory)
    {
        var warnings = new List<string>();
        var temporary = new List<string>();
        VideoSettings video = preset.Video;
        VideoEncoderInfo encoder = EncoderCatalog.FindVideo(video.Encoder)
            ?? throw new ArgumentException(Loc.T("encode.error.unknownEncoder", video.Encoder), nameof(preset));
        RateControl mode = encoder.Family == EncoderFamily.Copy ? RateControl.Copy : video.Mode;
        if (!encoder.Modes.Contains(mode))
        {
            throw new ArgumentException(Loc.T("encode.error.unsupportedMode", encoder.Name, mode), nameof(preset));
        }

        TimeSpan duration = Range(preset.Streams, info.Duration);
        string container = preset.Container.ToLowerInvariant();
        bool isMp4 = container is "mp4" or "mov";

        // Audio first: a target size has to leave room for it.
        var audioArgs = new List<string>();
        int audioKbps = AudioArguments(preset.Audio, preset.Custom.Audio, info, audioArgs, warnings);

        int videoKbps = 0;
        bool twoPass = false;
        if (mode == RateControl.TargetSize)
        {
            videoKbps = TargetVideoKbps(video.TargetMbPer24Minutes, duration, audioKbps);
            if (videoKbps <= 0)
            {
                throw new ArgumentException(Loc.T("encode.error.targetTooSmall"), nameof(preset));
            }

            twoPass = true;
        }
        else if (mode is RateControl.Bitrate or RateControl.TwoPass)
        {
            videoKbps = video.BitrateKbps;
            twoPass = mode == RateControl.TwoPass;
        }

        var head = new List<string> { "-hide_banner", "-nostdin", "-y" };
        head.AddRange(ArgumentTokenizer.Split(preset.Custom.Input));
        if (preset.Streams.Start is double start and > 0)
        {
            head.AddRange(["-ss", Seconds(start)]);
        }

        head.AddRange(["-i", source]);
        bool chapterInput = !string.IsNullOrWhiteSpace(preset.Streams.ChaptersFile);
        if (chapterInput)
        {
            head.AddRange(["-f", "ffmetadata", "-i", preset.Streams.ChaptersFile!]);
        }
        if (preset.Streams.Start is not null || preset.Streams.End is not null)
        {
            head.AddRange(["-t", Seconds(duration.TotalSeconds)]);
        }

        var videoArgs = new List<string> { "-map", "0:v:0" };
        if (mode == RateControl.Copy)
        {
            videoArgs.AddRange(["-c:v", "copy"]);
        }
        else
        {
            videoArgs.AddRange(["-c:v", encoder.Name]);
            string filters = Filters(preset, source, info, warnings);
            if (filters.Length > 0)
            {
                videoArgs.AddRange(["-vf", filters]);
            }

            Option(videoArgs, encoder.PresetOption, video.Preset ?? encoder.DefaultPreset);
            Option(videoArgs, encoder.TuneOption, video.Tune);
            Option(videoArgs, "-profile:v", video.Profile);
            Option(videoArgs, "-pix_fmt", video.PixelFormat ?? PixelFormat(encoder, video.BitDepth, warnings));
            if (video.KeyframeInterval > 0)
            {
                videoArgs.AddRange(["-g", video.KeyframeInterval.ToString(CultureInfo.InvariantCulture)]);
            }

            if (!string.IsNullOrWhiteSpace(video.FrameRate))
            {
                videoArgs.AddRange(["-r", video.FrameRate.Trim()]);
            }

            if (video.ConstantFrameRate)
            {
                videoArgs.AddRange(["-fps_mode", "cfr"]);
            }

            ColorTags(preset.Color, videoArgs);
            RateArguments(encoder, mode, video.Quality, videoKbps, videoArgs);
        }

        videoArgs.AddRange(ArgumentTokenizer.Split(preset.Custom.Video));

        var streamArgs = new List<string>();
        if (preset.Streams.KeepSubtitles)
        {
            if (isMp4)
            {
                warnings.Add(Loc.T("encode.warning.mp4Subtitles"));
            }
            else
            {
                streamArgs.AddRange(["-map", "0:s?", "-c:s", "copy"]);
            }
        }

        if (preset.Streams.KeepAttachments)
        {
            if (container == "mkv")
            {
                streamArgs.AddRange(["-map", "0:t?"]);
            }
            else
            {
                warnings.Add(Loc.T("encode.warning.attachments"));
            }
        }

        streamArgs.AddRange(["-map_chapters", chapterInput ? "1" : preset.Streams.KeepChapters ? "0" : "-1"]);
        // Dropping metadata means the global tags only: stream languages and chapter titles stay.
        streamArgs.AddRange(preset.Streams.KeepMetadata ? ["-map_metadata", "0"] : ["-map_metadata:g", "-1"]);
        if (!string.IsNullOrWhiteSpace(preset.Streams.Title))
        {
            streamArgs.AddRange(["-metadata", "title=" + preset.Streams.Title.Trim()]);
        }

        if (isMp4)
        {
            streamArgs.AddRange(["-movflags", "+faststart"]);
            if (encoder.Codec == "HEVC" && mode != RateControl.Copy)
            {
                streamArgs.AddRange(["-tag:v", "hvc1"]); // plays in Apple and most TV players
            }
        }

        var tail = new List<string>(ArgumentTokenizer.Split(preset.Custom.Output)) { "-progress", "pipe:1", "-nostats" };

        var passes = new List<IReadOnlyList<string>>();
        if (twoPass && encoder.Family is EncoderFamily.X264 or EncoderFamily.X265)
        {
            string log = Path.Combine(workDirectory, "vq-2pass-" + Guid.NewGuid().ToString("N"));
            temporary.Add(log);
            passes.Add([.. head, .. PassArguments(encoder, video, videoArgs, 1, log), "-an", "-sn", "-dn", .. tail, "-f", "null", NullDevice]);
            passes.Add([.. head, .. PassArguments(encoder, video, videoArgs, 2, log), .. audioArgs, .. streamArgs, .. tail, output]);
        }
        else
        {
            if (encoder.Family != EncoderFamily.Copy)
            {
                MergeParams(encoder, video.EncoderParams, null, videoArgs);
            }

            passes.Add([.. head, .. videoArgs, .. audioArgs, .. streamArgs, .. tail, output]);
        }

        // The output's frame rate lets the queue follow progress by frame count when ffmpeg cannot
        // report a time (it says N/A while a copied subtitle or font stream has no packets).
        double frameRate = mode != RateControl.Copy && ParseRate(video.FrameRate) is double forced and > 0
            ? forced
            : info.Video?.FrameRate ?? 0;
        return new EncodeCommand(passes, output, temporary, warnings, videoKbps, duration, frameRate);
    }

    /// <summary>The output path for a source: the preset's pattern, beside the source, with the container's extension.</summary>
    public static string OutputPath(EncodePreset preset, string source, string? folder = null)
    {
        string name = preset.OutputPattern.Replace("{name}", Path.GetFileNameWithoutExtension(source), StringComparison.Ordinal);
        string directory = folder ?? Path.GetDirectoryName(Path.GetFullPath(source))!;
        string path = Path.Combine(directory, name + "." + preset.Container.ToLowerInvariant());
        return string.Equals(Path.GetFullPath(path), Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(directory, name + ".out." + preset.Container.ToLowerInvariant())
            : path;
    }

    /// <summary>
    /// Video kbps that makes the whole file about <paramref name="megabytesPer24Minutes"/> MiB per 24 minutes,
    /// after audio and ~1% container overhead.
    /// </summary>
    public static int TargetVideoKbps(int megabytesPer24Minutes, TimeSpan duration, int audioKbps)
    {
        if (duration <= TimeSpan.Zero)
        {
            return 0;
        }

        double totalKbps = megabytesPer24Minutes * 1024.0 * 1024.0 * 8 / 1000 / (24 * 60);
        return (int)Math.Floor(totalKbps * 0.99) - audioKbps;
    }

    /// <summary>Quotes an argument list for display (and for pasting into a shell).</summary>
    public static string FormatCommandLine(string executable, IEnumerable<string> arguments) =>
        string.Join(' ', new[] { executable }.Concat(arguments).Select(Quote));

    /// <summary>Escapes a value for an option inside a filtergraph given as one argument (no shell involved).</summary>
    public static string FilterValue(string value)
    {
        // Level 1: the option value inside the filter's argument list.
        string level1 = value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal)
            .Replace(":", "\\:", StringComparison.Ordinal);

        // Level 2: the filter description inside the graph.
        var level2 = new StringBuilder(level1.Length + 8);
        foreach (char c in level1)
        {
            if (c is '\\' or '\'' or '[' or ']' or ',' or ';')
            {
                level2.Append('\\');
            }

            level2.Append(c);
        }

        return level2.ToString();
    }

    /// <summary>A path as ffmpeg's filters want it: forward slashes on every platform.</summary>
    public static string FilterPath(string path) => FilterValue(path.Replace('\\', '/'));

    private static string NullDevice => OperatingSystem.IsWindows() ? "NUL" : "/dev/null";

    /// <summary>A frame rate as ffmpeg takes it: <c>24000/1001</c> or <c>23.976</c>; null when it is neither.</summary>
    internal static double? ParseRate(string? text)
    {
        string[] parts = (text ?? string.Empty).Trim().Split('/');
        return parts.Length switch
        {
            1 when double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double rate) => rate,
            2 when double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double num) &&
                   double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double den) && den > 0 => num / den,
            _ => null,
        };
    }

    private static TimeSpan Range(StreamSettings streams, TimeSpan full)
    {
        double start = streams.Start ?? 0;
        double end = streams.End is double e && e > start ? e : full.TotalSeconds;
        return TimeSpan.FromSeconds(Math.Max(0, end - start));
    }

    private static int AudioArguments(AudioSettings audio, string? custom, MediaInfo info, List<string> args, List<string> warnings)
    {
        List<MediaStream> tracks = info.AudioStreams.ToList();
        if (audio.Encoder == "none" || tracks.Count == 0)
        {
            args.Add("-an");
            return 0;
        }

        if (audio.FirstTrackOnly)
        {
            tracks = [tracks[0]];
            args.AddRange(["-map", "0:a:0"]);
        }
        else
        {
            args.AddRange(["-map", "0:a"]);
        }

        if (!string.IsNullOrWhiteSpace(audio.Language))
        {
            for (int i = 0; i < tracks.Count; i++)
            {
                args.AddRange([$"-metadata:s:a:{i.ToString(CultureInfo.InvariantCulture)}", "language=" + audio.Language.Trim()]);
            }
        }

        if (audio.Encoder == "copy")
        {
            args.AddRange(["-c:a", "copy"]);
            args.AddRange(ArgumentTokenizer.Split(custom));
            return (int)(tracks.Sum(t => t.BitRate) / 1000);
        }

        AudioEncoderInfo encoder = EncoderCatalog.FindAudio(audio.Encoder)
            ?? throw new ArgumentException(Loc.T("encode.error.unknownEncoder", audio.Encoder), nameof(audio));
        int total = 0;
        for (int i = 0; i < tracks.Count; i++)
        {
            MediaStream track = tracks[i];
            string n = i.ToString(CultureInfo.InvariantCulture);
            bool compatible = audio.CopyWhenCompatible &&
                              string.Equals(track.Codec, encoder.Codec, StringComparison.OrdinalIgnoreCase) &&
                              (!encoder.HasBitrate || (track.BitRate > 0 && track.BitRate <= audio.BitrateKbps * 1000L * 105 / 100)) &&
                              (audio.SampleRate == 0 || track.SampleRate == audio.SampleRate) &&
                              (audio.Channels == 0 || track.Channels == audio.Channels);
            if (compatible)
            {
                args.AddRange([$"-c:a:{n}", "copy"]);
                total += (int)(track.BitRate / 1000);
                warnings.Add(Loc.T("encode.warning.audioCopied", track.Index));
                continue;
            }

            args.AddRange([$"-c:a:{n}", encoder.Name]);
            if (encoder.HasBitrate)
            {
                args.AddRange([$"-b:a:{n}", audio.BitrateKbps.ToString(CultureInfo.InvariantCulture) + "k"]);
                total += audio.BitrateKbps;
            }
            else
            {
                total += 1000; // lossless: a rough allowance for target-size maths
            }

            if (audio.SampleRate > 0)
            {
                args.AddRange([$"-ar:a:{n}", audio.SampleRate.ToString(CultureInfo.InvariantCulture)]);
            }

            if (audio.Channels > 0)
            {
                args.AddRange([$"-ac:a:{n}", audio.Channels.ToString(CultureInfo.InvariantCulture)]);
            }

            if (encoder.Name == "flac" && audio.SampleBits == 24)
            {
                args.AddRange([$"-sample_fmt:a:{n}", "s32", $"-bits_per_raw_sample:a:{n}", "24"]);
            }
        }

        args.AddRange(ArgumentTokenizer.Split(custom));
        return total;
    }

    private static string Filters(EncodePreset preset, string source, MediaInfo info, List<string> warnings)
    {
        FrameSettings frame = preset.Frame;
        var filters = new List<string>();
        if (frame.CropLeft + frame.CropRight + frame.CropTop + frame.CropBottom > 0)
        {
            filters.Add(string.Create(CultureInfo.InvariantCulture,
                $"crop=iw-{frame.CropLeft + frame.CropRight}:ih-{frame.CropTop + frame.CropBottom}:{frame.CropLeft}:{frame.CropTop}"));
        }

        switch (frame.Deinterlace)
        {
            case Deinterlace.Bwdif:
                filters.Add("bwdif=mode=send_frame");
                break;
            case Deinterlace.Yadif:
                filters.Add("yadif");
                break;
        }

        int strength = Math.Clamp(frame.DenoiseStrength, 1, 10);
        switch (frame.Denoise)
        {
            case Denoise.Hqdn3d:
                filters.Add(string.Create(CultureInfo.InvariantCulture, $"hqdn3d={strength}"));
                break;
            case Denoise.Nlmeans:
                filters.Add(string.Create(CultureInfo.InvariantCulture, $"nlmeans=s={strength}"));
                break;
        }

        if (frame.Deband)
        {
            filters.Add("deband");
        }

        if (frame.Width != 0 || frame.Height != 0)
        {
            int width = frame.Width == 0 ? -2 : frame.Width;
            int height = frame.Height == 0 ? -2 : frame.Height;
            filters.Add(string.Create(CultureInfo.InvariantCulture, $"scale={width}:{height}:flags={frame.ScaleAlgorithm}"));
        }

        switch (frame.Flip)
        {
            case Flip.Horizontal:
                filters.Add("hflip");
                break;
            case Flip.Vertical:
                filters.Add("vflip");
                break;
        }

        switch (frame.Rotate)
        {
            case 90:
                filters.Add("transpose=1");
                break;
            case 180:
                filters.Add("hflip,vflip");
                break;
            case 270:
                filters.Add("transpose=2");
                break;
        }

        if (frame.Sharpen > 0)
        {
            filters.Add(string.Create(CultureInfo.InvariantCulture, $"unsharp=5:5:{frame.Sharpen:0.##}"));
        }

        if (preset.Burn is { } burn && BurnFilter(burn, source, info, warnings) is { } subtitles)
        {
            filters.Add(subtitles);
        }

        if (!string.IsNullOrWhiteSpace(preset.Custom.Filter))
        {
            filters.Add(preset.Custom.Filter.Trim());
        }

        return string.Join(',', filters);
    }

    private static string? BurnFilter(SubtitleBurn burn, string source, MediaInfo info, List<string> warnings)
    {
        string? file = burn.File;
        int? streamIndex = null;
        if (string.IsNullOrWhiteSpace(file))
        {
            if (burn.StreamIndex is int index)
            {
                // Position among the subtitle streams, as the subtitles filter's "si" expects.
                List<MediaStream> subtitles = info.SubtitleStreams.ToList();
                int position = subtitles.FindIndex(s => s.Index == index);
                if (position < 0)
                {
                    warnings.Add(Loc.T("encode.warning.noSubtitleStream", index));
                    return null;
                }

                file = source;
                streamIndex = position;
            }
            else
            {
                string stem = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(source))!, Path.GetFileNameWithoutExtension(source));
                file = SubtitleExtensions.Select(e => stem + e).FirstOrDefault(File.Exists);
                if (file is null)
                {
                    warnings.Add(Loc.T("encode.warning.noSubtitleFile"));
                    return null;
                }
            }
        }

        var filter = new StringBuilder("subtitles=filename=").Append(FilterPath(file));
        if (streamIndex is int si)
        {
            filter.Append(":si=").Append(si.ToString(CultureInfo.InvariantCulture));
        }

        if (!string.IsNullOrWhiteSpace(burn.FontsDirectory))
        {
            filter.Append(":fontsdir=").Append(FilterPath(burn.FontsDirectory));
        }

        if (!string.IsNullOrWhiteSpace(burn.ForceStyle))
        {
            filter.Append(":force_style=").Append(FilterValue(burn.ForceStyle));
        }

        if (!string.IsNullOrWhiteSpace(burn.CharacterEncoding))
        {
            filter.Append(":charenc=").Append(FilterValue(burn.CharacterEncoding));
        }

        return filter.ToString();
    }

    private static string? PixelFormat(VideoEncoderInfo encoder, int bitDepth, List<string> warnings)
    {
        if (bitDepth == 0)
        {
            return null;
        }

        if (!encoder.BitDepths.Contains(bitDepth))
        {
            warnings.Add(Loc.T("encode.warning.bitDepth", encoder.Name, bitDepth));
            return null;
        }

        bool software = encoder.Family is EncoderFamily.X264 or EncoderFamily.X265 or EncoderFamily.SvtAv1;
        return (bitDepth, software) switch
        {
            (8, true) => "yuv420p",
            (10, true) => "yuv420p10le",
            (12, _) => "yuv420p12le",
            (8, false) => encoder.Family == EncoderFamily.Nvenc ? "yuv420p" : "nv12",
            _ => "p010le",
        };
    }

    private static void ColorTags(ColorSettings color, List<string> args)
    {
        Option(args, "-color_primaries", color.Primaries);
        Option(args, "-color_trc", color.Transfer);
        Option(args, "-colorspace", color.Matrix);
        Option(args, "-color_range", color.Range);
    }

    private static void RateArguments(VideoEncoderInfo encoder, RateControl mode, double quality, int kbps, List<string> args)
    {
        string q = quality.ToString("0.##", CultureInfo.InvariantCulture);
        string rate = kbps.ToString(CultureInfo.InvariantCulture) + "k";
        switch (encoder.Family, mode)
        {
            case (EncoderFamily.Nvenc, RateControl.Quality):
                args.AddRange(["-rc", "vbr", "-cq", q, "-b:v", "0"]);
                break;
            case (EncoderFamily.Nvenc, RateControl.Qp):
                args.AddRange(["-rc", "constqp", "-qp", q]);
                break;
            case (EncoderFamily.Nvenc, RateControl.TwoPass or RateControl.TargetSize):
                args.AddRange(["-rc", "vbr", "-multipass", "fullres", "-b:v", rate]);
                break;
            case (EncoderFamily.Nvenc, _):
                args.AddRange(["-rc", "vbr", "-b:v", rate]);
                break;
            case (EncoderFamily.Qsv, RateControl.Quality):
                args.AddRange(["-global_quality", q]);
                break;
            case (EncoderFamily.Amf, RateControl.Qp):
                args.AddRange(["-rc", "cqp", "-qp_i", q, "-qp_p", q, "-qp_b", q]);
                break;
            case (EncoderFamily.Amf, _):
                args.AddRange(["-rc", "vbr_peak", "-b:v", rate]);
                break;
            case (_, RateControl.Quality):
                args.AddRange(["-crf", q]);
                break;
            case (_, RateControl.Qp):
                args.AddRange(["-qp", q]);
                break;
            default:
                args.AddRange(["-b:v", rate]);
                break;
        }
    }

    private static List<string> PassArguments(VideoEncoderInfo encoder, VideoSettings video, List<string> videoArgs, int pass, string log)
    {
        var args = new List<string>(videoArgs);
        string number = pass.ToString(CultureInfo.InvariantCulture);
        if (encoder.Family == EncoderFamily.X265)
        {
            // ':' separates x265 options, so the drive letter's colon (D:/…) is escaped.
            string stats = log.Replace('\\', '/').Replace(":", "\\:", StringComparison.Ordinal);
            MergeParams(encoder, video.EncoderParams, $"pass={number}:stats={stats}", args);
        }
        else
        {
            args.AddRange(["-pass", number, "-passlogfile", log]);
            MergeParams(encoder, video.EncoderParams, null, args);
        }

        return args;
    }

    private static void MergeParams(VideoEncoderInfo encoder, string? userParams, string? extra, List<string> args)
    {
        string merged = string.Join(':', new[] { userParams?.Trim(), extra }.Where(p => !string.IsNullOrEmpty(p)));
        if (merged.Length > 0 && encoder.ParamsOption is { } option)
        {
            args.AddRange([option, merged]);
        }
    }

    private static void Option(List<string> args, string option, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            args.AddRange([option, value.Trim()]);
        }
    }

    private static string Seconds(double seconds) => seconds.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Quote(string argument) =>
        argument.Length > 0 && argument.All(c => char.IsLetterOrDigit(c) || "-_./:=+,@%".Contains(c))
            ? argument
            : "\"" + argument.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
