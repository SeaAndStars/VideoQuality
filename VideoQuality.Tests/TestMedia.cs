using VideoQuality.Core.Tools;

namespace VideoQuality.Tests;

/// <summary>
/// Small real clips generated once per test run with the encoder ffmpeg, so integration tests need
/// no checked-in media. The encoder comes from <c>VQ_ENCODER_FFMPEG</c> (CI fetches the bundled
/// build with scripts/fetch-ffmpeg-gpl.sh) or an ffmpeg on PATH that has libx265.
/// </summary>
public static class TestMedia
{
    private static readonly Lazy<string?> Encoder = new(FindEncoder);
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "vq-test-media-" + Environment.ProcessId);

    public static string? EncoderPath => Encoder.Value;

    /// <summary>Fails the test (not skips) when no encoder is available: coverage depends on these tests.</summary>
    public static string RequireEncoder() =>
        EncoderPath ?? throw new InvalidOperationException("Set VQ_ENCODER_FFMPEG to a GPL ffmpeg (scripts/fetch-ffmpeg-gpl.sh).");

    /// <summary>A 2-second 320×180 23.976 fps H.264 clip with a stereo AAC track and chapters (MKV).</summary>
    public static Task<string> ClipAsync() => MakeAsync("clip.mkv", path =>
    [
        "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=24000/1001:duration=2",
        "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=2",
        "-f", "ffmetadata", "-i", ChaptersFile(),
        "-map", "0:v", "-map", "1:a", "-map_chapters", "2",
        "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
        "-c:a", "aac", "-b:a", "128k", "-ac", "2",
        "-metadata:s:a:0", "language=jpn",
        path,
    ]);

    private static string[] Source(int seconds, string size = "320x180") =>
        ["-f", "lavfi", "-i", $"testsrc2=size={size}:rate=24000/1001:duration={seconds}"];

    /// <summary>HEVC 10-bit with BT.709 limited-range tags (MKV).</summary>
    public static Task<string> Hevc10Async() => MakeAsync("hevc10.mkv", path =>
        [.. Source(2), "-c:v", "libx265", "-preset", "ultrafast", "-pix_fmt", "yuv420p10le", "-x265-params", "log-level=error",
         "-color_primaries", "bt709", "-color_trc", "bt709", "-colorspace", "bt709", "-color_range", "tv", path]);

    /// <summary>Lossless FFV1, full range, BT.601 tags (MKV).</summary>
    public static Task<string> LosslessAsync() => MakeAsync("ffv1.mkv", path =>
        [.. Source(1), "-c:v", "ffv1", "-pix_fmt", "yuv420p", "-colorspace", "bt470bg", "-color_range", "pc", path]);

    /// <summary>A raw H.264 elementary stream: no container, no timestamps, no duration.</summary>
    public static Task<string> RawH264Async() => MakeAsync("raw.h264", path =>
        [.. Source(1), "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-bsf:v", "h264_mp4toannexb", "-f", "h264", path]);

    /// <summary>H.264 in MP4 (the container states the frame count).</summary>
    public static Task<string> Mp4Async() => MakeAsync("clip.mp4", path =>
        [.. Source(2), "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-colorspace", "smpte170m", path]);

    /// <summary>AV1 (decoded by dav1d), BT.2020 tags (MKV).</summary>
    public static Task<string> Av1Async() => MakeAsync("av1.mkv", path =>
        [.. Source(1), "-c:v", "libsvtav1", "-preset", "12", "-pix_fmt", "yuv420p", "-colorspace", "bt2020nc", path]);

    /// <summary>15 seconds (359 frames) with a keyframe every 48 frames, for long seeks.</summary>
    /// <summary>
    /// Variable frame rate (MKV): a burst of frames sharing timestamps, then frames two apart, so
    /// timestamp-derived frame numbers both collide and skip.
    /// </summary>
    public static Task<string> VfrAsync() => MakeAsync("vfr.mkv", path =>
        ["-f", "lavfi", "-i", "testsrc2=size=160x90:rate=24:duration=2", "-vf", "setpts='if(lt(N,12),N/96,(N-9)/24*2)/TB'",
         "-fps_mode", "passthrough", "-c:v", "libx264", "-preset", "ultrafast", "-g", "12", "-bf", "0", path]);

    public static Task<string> LongAsync() => MakeAsync("long.mkv", path =>
        [.. Source(15), "-c:v", "libx264", "-preset", "ultrafast", "-g", "48", "-pix_fmt", "yuv420p", path]);

    /// <summary>
    /// Video, audio, a forced Chinese ASS track named JPSC and a font attachment (MKV): every kind of
    /// Matroska stream a release has.
    /// </summary>
    public static Task<string> RichAsync() => MakeAsync("rich.mkv", path =>
    [
        .. Source(1), "-f", "lavfi", "-i", "sine=duration=1", "-i", Script(Root, "rich.ass"),
        "-map", "0:v", "-map", "1:a", "-map", "2:s",
        "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "flac", "-c:s", "copy",
        "-metadata:s:s:0", "language=chi", "-metadata:s:s:0", "title=JPSC", "-disposition:s:0", "default+forced",
        "-attach", Path.Combine(AppContext.BaseDirectory, "Fixtures", "fonts", "DejaVuSans.ttf"), "-metadata:s:t", "mimetype=font/ttf",
        "-metadata", "title=Rich", path,
    ]);

    /// <summary>MP3 with cover art (an attached picture: a video stream that is not video).</summary>
    public static Task<string> CoverAsync() => MakeAsync("cover.mp3", path =>
    [
        "-f", "lavfi", "-i", "sine=duration=1", "-f", "lavfi", "-i", "color=c=red:s=64x64:d=1",
        "-map", "0:a", "-map", "1:v", "-frames:v", "1", "-c:a", "libmp3lame", "-c:v", "png", "-disposition:v", "attached_pic", path,
    ]);

    /// <summary>MPEG-TS with a video and a raw data stream.</summary>
    public static Task<string> DataStreamAsync() => MakeAsync("data.ts", path =>
    {
        string data = Path.Combine(Root, "data.bin");
        File.WriteAllBytes(data, Enumerable.Range(0, 2000).Select(i => (byte)i).ToArray());
        return [.. Source(1), "-f", "data", "-i", data, "-map", "0:v", "-map", "1:d", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:d", "copy", path];
    });

    /// <summary>Audio only (no video stream).</summary>
    public static Task<string> AudioOnlyAsync() => MakeAsync("audio.mka", path =>
        ["-f", "lavfi", "-i", "sine=duration=1", "-c:a", "flac", path]);

    /// <summary>An ASS script that draws "TEST" big and white across the whole 2-second clip.</summary>
    public static string Script(string directory, string name = "clip.ass")
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, name);
        File.WriteAllText(path, """
            [Script Info]
            ScriptType: v4.00+
            PlayResX: 320
            PlayResY: 180

            [V4+ Styles]
            Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
            Style: Default,DejaVu Sans,60,&H00FFFFFF,&H00FFFFFF,&H00000000,&H00000000,-1,0,0,0,100,100,0,0,1,0,0,5,0,0,0,1

            [Events]
            Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
            Dialogue: 0,0:00:00.00,0:00:05.00,Default,,0,0,0,,{\p1}m 0 0 l 320 0 320 180 0 180{\p0}
            """);
        return path;
    }

    private static string ChaptersFile()
    {
        Directory.CreateDirectory(Root);
        string path = Path.Combine(Root, "chapters.txt");
        File.WriteAllText(path, """
            ;FFMETADATA1
            [CHAPTER]
            TIMEBASE=1/1000
            START=0
            END=1000
            title=OP
            [CHAPTER]
            TIMEBASE=1/1000
            START=1000
            END=2000
            title=Chapter1
            """);
        return path;
    }

    private static async Task<string> MakeAsync(string name, Func<string, string[]> arguments)
    {
        string path = Path.Combine(Root, name);
        await Gate.WaitAsync();
        try
        {
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(Root);
                ProcessResult result = await SystemProcessRunner.Instance.RunAsync(new ProcessRequest
                {
                    FileName = RequireEncoder(),
                    Arguments = ["-hide_banner", "-y", .. arguments(path)],
                });
                Assert.True(result.ExitCode == 0, result.Error);
            }

            return path;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static string? FindEncoder()
    {
        if (Environment.GetEnvironmentVariable("VQ_ENCODER_FFMPEG") is { Length: > 0 } configured && File.Exists(configured))
        {
            return configured;
        }

        EncoderTool? tool = EncoderTool.LocateAsync(null, SystemProcessRunner.Instance).GetAwaiter().GetResult();
        return tool is not null && tool.HasEncoder("libx265") ? tool.Path : null;
    }
}
