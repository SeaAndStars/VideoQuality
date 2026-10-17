using System.Globalization;
using System.Text;
using VideoQuality.Core.Encode;
using VideoQuality.Core.Localization;
using VideoQuality.Core.Media;

namespace VideoQuality.Core.Edit;

/// <summary>A cut or a join, as the program and argument list that performs it.</summary>
public sealed record EditCommand(
    bool UsesMkvMerge,
    IReadOnlyList<string> Arguments,
    string Output,
    IReadOnlyList<string> TemporaryFiles,
    IReadOnlyList<string> Warnings,
    TimeSpan Duration);

/// <summary>Cutting a range out of a file (切割) and joining files end to end (合并).</summary>
public static class TrimMerge
{
    /// <summary>
    /// Cuts <paramref name="start"/>–<paramref name="end"/> without re-encoding. A stream copy can only
    /// start on a keyframe, so the cut starts at the keyframe at or before <paramref name="start"/>
    /// (named in a warning when that differs by more than a frame).
    /// </summary>
    /// <param name="keyframes">Keyframe times of the video, when known (from the packet index).</param>
    public static EditCommand CopyCut(string source, string output, TimeSpan start, TimeSpan end, IReadOnlyList<TimeSpan>? keyframes = null)
    {
        if (end <= start)
        {
            throw new ArgumentException(Loc.T("edit.error.range"), nameof(end));
        }

        var warnings = new List<string>();
        if (keyframes is { Count: > 0 })
        {
            TimeSpan snapped = keyframes.Where(k => k <= start).DefaultIfEmpty(TimeSpan.Zero).Max();
            if (start - snapped > TimeSpan.FromMilliseconds(50))
            {
                warnings.Add(Loc.T("edit.warning.keyframe", ChapterTime(snapped), ChapterTime(start)));
            }
        }

        string[] args =
        [
            "-hide_banner", "-nostdin", "-y",
            "-ss", Seconds(start), "-i", source, "-t", Seconds(end - start),
            "-map", "0", "-c", "copy", "-avoid_negative_ts", "make_zero",
            "-progress", "pipe:1", "-nostats", output,
        ];
        return new EditCommand(false, args, output, [], warnings, end - start);
    }

    /// <summary>Cuts by re-encoding with a preset: frame-exact, at the cost of an encode.</summary>
    public static EncodeCommand EncodeCut(EncodePreset preset, string source, MediaInfo info, string output, string workDirectory, TimeSpan start, TimeSpan end)
    {
        if (end <= start)
        {
            throw new ArgumentException(Loc.T("edit.error.range"), nameof(end));
        }

        EncodePreset cut = preset with { Streams = preset.Streams with { Start = start.TotalSeconds, End = end.TotalSeconds } };
        return FfmpegCommandBuilder.Build(cut, source, info, output, workDirectory);
    }

    /// <summary>Why these files cannot be joined without re-encoding (empty when they can).</summary>
    public static IReadOnlyList<string> Compatibility(IReadOnlyList<MediaInfo> inputs)
    {
        var problems = new List<string>();
        if (inputs.Count < 2)
        {
            problems.Add(Loc.T("edit.merge.needTwo"));
            return problems;
        }

        MediaInfo first = inputs[0];
        foreach (MediaInfo other in inputs.Skip(1))
        {
            string name = Path.GetFileName(other.Path);
            if (Shape(first.Video) != Shape(other.Video))
            {
                problems.Add(Loc.T("edit.merge.video", name, Shape(other.Video), Shape(first.Video)));
            }

            string audio = string.Join(", ", first.AudioStreams.Select(AudioShape));
            string otherAudio = string.Join(", ", other.AudioStreams.Select(AudioShape));
            if (audio != otherAudio)
            {
                problems.Add(Loc.T("edit.merge.audio", name, otherAudio, audio));
            }
        }

        return problems;
    }

    /// <summary>
    /// Joins files end to end without re-encoding: mkvmerge's append for Matroska output (keeps
    /// chapters, attachments and timestamps right), ffmpeg's concat demuxer for anything else.
    /// </summary>
    public static EditCommand Merge(IReadOnlyList<MediaInfo> inputs, string output, string workDirectory)
    {
        IReadOnlyList<string> problems = Compatibility(inputs);
        if (inputs.Count < 2)
        {
            throw new ArgumentException(problems[0], nameof(inputs));
        }

        TimeSpan duration = inputs.Aggregate(TimeSpan.Zero, (total, i) => total + i.Duration);
        if (output.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase))
        {
            var args = new List<string> { "--gui-mode", "-o", output, inputs[0].Path };
            foreach (MediaInfo input in inputs.Skip(1))
            {
                args.Add("+" + input.Path);
            }

            return new EditCommand(true, args, output, [], problems, duration);
        }

        string list = Path.Combine(workDirectory, "vq-concat-" + Guid.NewGuid().ToString("N") + ".txt");
        var text = new StringBuilder("ffconcat version 1.0\n");
        foreach (MediaInfo input in inputs)
        {
            // Single-quoted, with ' written as '\'' (the concat demuxer's escaping).
            text.Append("file '").Append(Path.GetFullPath(input.Path).Replace("'", "'\\''", StringComparison.Ordinal)).Append("'\n");
        }

        Directory.CreateDirectory(workDirectory);
        File.WriteAllText(list, text.ToString(), new UTF8Encoding(false));
        string[] arguments =
        [
            "-hide_banner", "-nostdin", "-y", "-f", "concat", "-safe", "0", "-i", list,
            "-map", "0", "-c", "copy", "-progress", "pipe:1", "-nostats", output,
        ];
        return new EditCommand(false, arguments, output, [list], problems, duration);
    }

    private static string Shape(MediaStream? video) =>
        video is null ? "-" : $"{video.Codec} {video.Width}×{video.Height} {video.PixelFormat} {video.FrameRate.ToString("0.###", CultureInfo.InvariantCulture)}";

    private static string AudioShape(MediaStream audio) => $"{audio.Codec} {audio.SampleRate} {audio.Channels}ch";

    private static string Seconds(TimeSpan time) => time.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);

    private static string ChapterTime(TimeSpan time) => MediaInfo.FormatClock(time);
}
