using FFmpeg.AutoGen;
using VideoQuality.Core;
using VideoQuality.Core.Decoding;
using VideoQuality.Core.Diagnostics;
using VideoQuality.Core.Media;
using VideoQuality.Core.Tools;

namespace VideoQuality.Tests;

[Collection("ffmpeg")]
public sealed class FfmpegRuntimeTests : IDisposable
{
    private readonly string _work = Directory.CreateTempSubdirectory("vq-runtime-").FullName;

    public void Dispose() => Directory.Delete(_work, true);

    [Fact]
    public void Candidates_follow_the_environment()
    {
        var env = new Dictionary<string, string?>
        {
            ["FFMPEG_LIB_PATH"] = "/lib/path",
            ["FFMPEG_ROOT"] = "/root/ff",
            ["VQ_FFMPEG"] = "/tools/bin/ffmpeg",
        };
        List<string> all = [.. FfmpegRuntime.Candidates(name => env.GetValueOrDefault(name))];
        Assert.Equal(["/lib/path", "/root/ff", Path.Combine("/root/ff", "bin"), Path.Combine("/root/ff", "lib"), "/tools/bin"], all.Take(5));
        Assert.Contains(AppPaths.BundledFfmpegDirectory, all);
        Assert.Equal(FfmpegRuntime.SystemFolders, all.TakeLast(FfmpegRuntime.SystemFolders.Count));

        List<string> none = [.. FfmpegRuntime.Candidates(_ => null)];
        Assert.Equal([string.Empty, string.Empty], none.Take(2));
    }

    [Fact]
    public void Library_folder_prefers_the_expected_version_then_any_version()
    {
        string exact = Directory.CreateDirectory(Path.Combine(_work, "exact")).FullName;
        string other = Directory.CreateDirectory(Path.Combine(_work, "other")).FullName;
        string empty = Directory.CreateDirectory(Path.Combine(_work, "empty")).FullName;
        File.WriteAllText(Path.Combine(exact, OperatingSystem.IsWindows() ? $"avcodec-{FfmpegRuntime.ExpectedAvcodecMajor}.dll" : $"libavcodec.so.{FfmpegRuntime.ExpectedAvcodecMajor}"), string.Empty);
        File.WriteAllText(Path.Combine(other, OperatingSystem.IsWindows() ? "avcodec-60.dll" : "libavcodec.so.60"), string.Empty);

        Assert.Equal(exact, FfmpegRuntime.FindLibraryDirectory(["", Path.Combine(_work, "missing"), empty, other, exact]));
        Assert.Equal(other, FfmpegRuntime.FindLibraryDirectory([empty, other, other]));
        Assert.Null(FfmpegRuntime.FindLibraryDirectory([empty]));
    }

    [Fact]
    public void Loading_reports_missing_libraries_and_version_mismatches()
    {
        Assert.Equal(FfmpegRuntime.ExpectedAvcodecMajor, FfmpegRuntime.ValidateLoaded(() => FfmpegRuntime.ExpectedAvcodecMajor, "/x"));
        InvalidOperationException mismatch = Assert.Throws<InvalidOperationException>(() => FfmpegRuntime.ValidateLoaded(() => 60, null));
        Assert.Contains("60", mismatch.Message, StringComparison.Ordinal);
        Assert.Contains(FfmpegRuntime.Describe(null), mismatch.Message, StringComparison.Ordinal);
        InvalidOperationException missing = Assert.Throws<InvalidOperationException>(() => FfmpegRuntime.ValidateLoaded(() => throw new NotSupportedException("no dll"), "/libs"));
        Assert.IsType<NotSupportedException>(missing.InnerException);
        Assert.Contains("/libs", missing.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Directory_is_applied_and_windows_search_path_failures_are_logged()
    {
        var roots = new List<string>();
        FfmpegRuntime.UseDirectory(null, roots.Add, _ => throw new InvalidOperationException("not called"));
        Assert.Empty(roots);
        FfmpegRuntime.UseDirectory("/a", roots.Add, null);
        FfmpegRuntime.UseDirectory("/b", roots.Add, _ => true);
        FfmpegRuntime.UseDirectory("/c", roots.Add, _ => false);
        Assert.Equal(["/a", "/b", "/c"], roots);
    }

    [Fact]
    public void Log_lines_are_filtered_and_routed_by_severity()
    {
        FfmpegRuntime.WriteLogLine(ffmpeg.AV_LOG_ERROR, null);
        FfmpegRuntime.WriteLogLine(ffmpeg.AV_LOG_ERROR, "   ");
        FfmpegRuntime.WriteLogLine(ffmpeg.AV_LOG_WARNING, "Could not find codec parameters for stream 2 (Attachment: none): unknown codec");
        FfmpegRuntime.WriteLogLine(ffmpeg.AV_LOG_ERROR, "test error line\n");
        FfmpegRuntime.WriteLogLine(ffmpeg.AV_LOG_WARNING, "test warning line");
        Assert.False(FfmpegRuntime.IsNoise("Could not find codec parameters for stream 0 (Video: h264)"));
    }

    [Fact]
    public async Task Initialize_is_idempotent_and_describes_the_loaded_library()
    {
        FfmpegRuntime.Initialize();
        FfmpegRuntime.Initialize();
        FfmpegRuntime.InstallAvLogCallback();
        Assert.Contains("avcodec", FfmpegRuntime.Describe(), StringComparison.Ordinal);
        Assert.False(string.IsNullOrEmpty(FfmpegRuntime.ErrorText(-2)));

        // Opening a file with font attachments goes through the libav log callback (debug lines are
        // dropped by level, the attachment warning as noise).
        Assert.NotEmpty(MediaInfoReader.Read(await TestMedia.RichAsync()).Attachments);
    }
}

public sealed class LogTests : IDisposable
{
    private readonly string _work = Directory.CreateTempSubdirectory("vq-log-").FullName;

    public void Dispose() => Directory.Delete(_work, true);

    [Fact]
    public void Folders_follow_overrides_and_platform_conventions()
    {
        Assert.Equal(Path.Combine(AppPaths.Root, "logs"), VqLog.DefaultDirectoryFor(null));
        Assert.Equal(Path.Combine(AppPaths.Root, "logs"), VqLog.DefaultDirectoryFor("  "));
        Assert.Equal("/custom", VqLog.DefaultDirectoryFor("/custom"));
        Assert.EndsWith(Path.Combine("VideoQuality", "logs"), VqLog.StateDirectoryFor(true, _ => null), StringComparison.Ordinal);
        Assert.Equal(Path.Combine("/state", "videoquality", "logs"), VqLog.StateDirectoryFor(false, _ => "/state"));
        Assert.EndsWith(Path.Combine(".local", "state", "videoquality", "logs"), VqLog.StateDirectoryFor(false, _ => " "), StringComparison.Ordinal);
        Assert.False(string.IsNullOrEmpty(VqLog.DefaultDirectory));
        Assert.False(string.IsNullOrEmpty(VqLog.StateDirectory));
    }

    [Fact]
    public void Log_file_uses_the_first_writable_folder_and_never_throws()
    {
        string blocker = Path.Combine(_work, "a-file");
        File.WriteAllText(blocker, string.Empty);
        string unwritable = Path.Combine(blocker, "logs"); // a file in the way: IOException
        string denied = OperatingSystem.IsWindows() ? Path.Combine(Environment.SystemDirectory, "vq-test-logs") : "/sys/vq-test-logs";
        string good = Path.Combine(_work, "logs");

        Assert.Null(LogFile.Prepare(unwritable));
        Assert.Null(LogFile.Prepare(denied));
        LogFile file = LogFile.Open(unwritable, denied, good);
        Assert.StartsWith(good, file.Path, StringComparison.Ordinal);

        file.Write("INF", "test", "hello", null);
        file.Write("ERR", "test", "boom", new InvalidOperationException("inner"));
        string text = File.ReadAllText(file.Path!);
        Assert.Contains("[INF] test: hello", text, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException: inner", text, StringComparison.Ordinal);

        Assert.Null(LogFile.Open(unwritable).Path);
        new LogFile(null).Write("INF", "x", "dropped", null);
        new LogFile(_work).Write("INF", "x", "a directory: UnauthorizedAccessException", null);
        new LogFile(OperatingSystem.IsWindows() ? Path.Combine(blocker, "x.log") : "/proc/version").Write("INF", "x", "IOException", null);
    }

    [Fact]
    public void The_shared_log_writes_session_headers()
    {
        VqLog.Reset();
        VqLog.Info("test", "opens the default log");
        VqLog.Reset();
        VqLog.Initialize();
        VqLog.Reset();
        VqLog.Initialize(_work);
        VqLog.Initialize(); // already open: ignored
        Assert.StartsWith(_work, VqLog.CurrentPath, StringComparison.Ordinal);
        VqLog.SessionHeader("test session", new Dictionary<string, string> { ["key"] = "value" });
        VqLog.Warn("test", "warning");
        VqLog.Error("test", "error", new InvalidOperationException("x"));
        Assert.NotNull(VqLog.CurrentPath);
        Assert.Contains("key: value", File.ReadAllText(VqLog.CurrentPath!), StringComparison.Ordinal);
        VqLog.Disable();
        Assert.Null(VqLog.CurrentPath);
        VqLog.Info("test", "dropped");
        VqLog.Reset();
    }
}

public sealed class SafeEnumerationTests : IDisposable
{
    private readonly string _work = Directory.CreateTempSubdirectory("vq-enum-").FullName;

    public void Dispose()
    {
        foreach (string dir in Directory.EnumerateDirectories(_work))
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        Directory.Delete(_work, true);
    }

    [Fact]
    public void Unreadable_folders_and_files_enumerate_as_empty()
    {
        File.WriteAllText(Path.Combine(_work, "a.txt"), string.Empty);
        Assert.Single(FileSystem.SafeEnumerateFiles(_work, "*.txt"));
        Assert.Empty(FileSystem.SafeEnumerateFiles(Path.Combine(_work, "a.txt"), "*")); // not a folder: IOException

        // A folder the user may not list (as root on Linux this is readable anyway).
        string denied = OperatingSystem.IsWindows() ? @"C:\System Volume Information" : Directory.CreateDirectory(Path.Combine(_work, "denied")).FullName;
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(denied, UnixFileMode.None);
        }

        Assert.Empty(FileSystem.SafeEnumerateFiles(denied, "*"));
    }
}

[Collection("ffmpeg")]
public class PacketIndexAndProbeTests
{
    [Fact]
    public async Task Index_counts_packets_keyframes_and_bitrate()
    {
        PacketIndex index = PacketIndex.Build(await TestMedia.LongAsync());
        Assert.Equal(360, index.FrameCount);
        Assert.Equal(15, index.DurationSeconds, 0);
        Assert.Equal(0, index.KeyframeIndices[0]);
        Assert.True(index.KeyframeIndices.Count >= 7);
        Assert.True(index.BitrateKbps > 0);

        PacketIndex raw = PacketIndex.Build(await TestMedia.RawH264Async());
        Assert.Equal(24, raw.FrameCount);
        Assert.Equal(0, raw.BitrateKbps); // no duration

        PacketIndex interleaved = PacketIndex.Build(await TestMedia.ClipAsync()); // audio packets are skipped
        Assert.Equal(48, interleaved.FrameCount);
    }

    [Fact]
    public async Task Probe_reads_size_format_depth_and_rate()
    {
        MediaProbeResult hevc = MediaProbe.Probe(await TestMedia.Hevc10Async());
        Assert.Equal((320, 180, "yuv420p10le", 10), (hevc.Width, hevc.Height, hevc.PixelFormat, hevc.BitDepth));
        Assert.Equal(23.976, hevc.FrameRate, 3);
    }

    [Fact]
    public async Task Index_and_probe_reject_files_without_video()
    {
        string audio = await TestMedia.AudioOnlyAsync();
        Assert.Throws<InvalidOperationException>(() => PacketIndex.Build(audio));
        Assert.Throws<InvalidOperationException>(() => MediaProbe.Probe(audio));
        string junk = Path.Combine(Path.GetTempPath(), "vq-junk-" + Guid.NewGuid().ToString("N") + ".mkv");
        File.WriteAllText(junk, "junk");
        Assert.Throws<InvalidOperationException>(() => PacketIndex.Build(junk));
        Assert.Throws<InvalidOperationException>(() => MediaProbe.Probe(junk));
        File.Delete(junk);
    }

    [Fact]
    public async Task Media_info_classifies_every_kind_of_stream()
    {
        MediaInfo rich = MediaInfoReader.Read(await TestMedia.RichAsync());
        Assert.Equal("Rich", rich.Title);
        MediaStream subtitle = Assert.Single(rich.SubtitleStreams);
        Assert.Equal(("ass", "chi", "JPSC", true, true), (subtitle.Codec, subtitle.Language, subtitle.Title, subtitle.Default, subtitle.Forced));
        MediaStream font = Assert.Single(rich.Attachments);
        Assert.Equal(("DejaVuSans.ttf", "font/ttf"), (font.FileName, font.MimeType));
        Assert.Equal("flac", Assert.Single(rich.AudioStreams).Codec);

        MediaInfo cover = MediaInfoReader.Read(await TestMedia.CoverAsync());
        Assert.Null(cover.Video);
        Assert.Equal("png", Assert.Single(cover.Attachments).Codec);

        MediaInfo data = MediaInfoReader.Read(await TestMedia.DataStreamAsync());
        Assert.Contains(data.Streams, s => s.Kind == StreamKind.Data);

        MediaInfo raw = MediaInfoReader.Read(await TestMedia.RawH264Async());
        Assert.Equal(TimeSpan.Zero, raw.Duration);
    }
}
