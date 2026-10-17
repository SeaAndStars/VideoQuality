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
