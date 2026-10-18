using VideoQuality.Core.Encode;
using VideoQuality.Core.Media;

namespace VideoQuality.Tests;

public class EncodeCommandTests
{
    private static readonly MediaInfo Source = new()
    {
        Path = "/media/in.mkv",
        Format = "matroska,webm",
        Duration = TimeSpan.FromMinutes(24),
        Streams =
        [
            new MediaStream { Index = 0, Kind = StreamKind.Video, Codec = "h264", Width = 1920, Height = 1080 },
            new MediaStream { Index = 1, Kind = StreamKind.Audio, Codec = "opus", BitRate = 128_000, SampleRate = 48000, Channels = 2 },
            new MediaStream { Index = 2, Kind = StreamKind.Audio, Codec = "flac", SampleRate = 48000, Channels = 2 },
            new MediaStream { Index = 3, Kind = StreamKind.Subtitle, Codec = "ass" },
            new MediaStream { Index = 4, Kind = StreamKind.Subtitle, Codec = "ass" },
        ],
    };

    private static IReadOnlyList<string> Single(EncodePreset preset, MediaInfo? info = null) =>
        FfmpegCommandBuilder.Build(preset, "/media/in.mkv", info ?? Source, "/out/o.mkv", "/tmp").Passes.Single();

    private static string After(IReadOnlyList<string> args, string option) => args[args.ToList().IndexOf(option) + 1];

    [Fact]
    public void Default_preset_is_x265_crf_with_cfr_and_aac()
    {
        IReadOnlyList<string> args = Single(new EncodePreset());

        Assert.Equal(["-hide_banner", "-nostdin", "-y", "-i", "/media/in.mkv"], args.Take(5));
        Assert.Equal("libx265", After(args, "-c:v"));
        Assert.Equal("20", After(args, "-crf"));
        Assert.Equal("slow", After(args, "-preset"));
        Assert.Equal("cfr", After(args, "-fps_mode"));
        Assert.Equal("aac", After(args, "-c:a:0"));
        Assert.Equal("320k", After(args, "-b:a:0"));
        Assert.Equal("48000", After(args, "-ar:a:0"));
        Assert.Equal("aac", After(args, "-c:a:1"));
        Assert.Equal(["-progress", "pipe:1", "-nostats", "/out/o.mkv"], args.TakeLast(4));
        Assert.DoesNotContain("-vf", args);
    }

    [Fact]
    public void Compatible_audio_is_copied()
    {
        MediaInfo aacSource = Source with
        {
            Streams = [.. Source.Streams.Select(s => s.Index == 1 ? s with { Codec = "aac" } : s)],
        };
        EncodeCommand command = FfmpegCommandBuilder.Build(
            new EncodePreset { Audio = new AudioSettings { Encoder = "aac", BitrateKbps = 192, SampleRate = 48000, Channels = 2 } },
            "/media/in.mkv", aacSource, "/out/o.mkv", "/tmp");
        IReadOnlyList<string> args = command.Passes.Single();

        Assert.Equal("copy", After(args, "-c:a:0"));
        Assert.Equal("aac", After(args, "-c:a:1"));
        Assert.Contains(command.Warnings, w => w.Contains("#1", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("libx265", RateControl.Quality, new[] { "-crf", "21.5" })]
    [InlineData("libx264", RateControl.Qp, new[] { "-qp", "21.5" })]
    [InlineData("libsvtav1", RateControl.Bitrate, new[] { "-b:v", "3000k" })]
    [InlineData("hevc_nvenc", RateControl.Quality, new[] { "-rc", "vbr", "-cq", "21.5", "-b:v", "0" })]
    [InlineData("hevc_nvenc", RateControl.Qp, new[] { "-rc", "constqp", "-qp", "21.5" })]
    [InlineData("hevc_nvenc", RateControl.Bitrate, new[] { "-rc", "vbr", "-b:v", "3000k" })]
    [InlineData("h264_nvenc", RateControl.TwoPass, new[] { "-rc", "vbr", "-multipass", "fullres", "-b:v", "3000k" })]
    [InlineData("hevc_qsv", RateControl.Quality, new[] { "-global_quality", "21.5" })]
    [InlineData("hevc_qsv", RateControl.Bitrate, new[] { "-b:v", "3000k" })]
    [InlineData("hevc_amf", RateControl.Qp, new[] { "-rc", "cqp", "-qp_i", "21.5", "-qp_p", "21.5", "-qp_b", "21.5" })]
    [InlineData("h264_amf", RateControl.Bitrate, new[] { "-rc", "vbr_peak", "-b:v", "3000k" })]
    public void Rate_control_is_spelled_per_encoder_family(string encoder, RateControl mode, string[] expected)
    {
        string joined = string.Join(' ', Single(new EncodePreset
        {
            Video = new VideoSettings { Encoder = encoder, Mode = mode, Quality = 21.5, BitrateKbps = 3000 },
        }));

        Assert.Contains(string.Join(' ', expected), joined, StringComparison.Ordinal);
    }

    [Fact]
    public void Copy_encoder_copies_video_without_filters_or_rate()
    {
        IReadOnlyList<string> args = Single(new EncodePreset
        {
            Video = new VideoSettings { Encoder = "copy", Mode = RateControl.Quality },
            Frame = new FrameSettings { Width = 1280 },
            Audio = new AudioSettings { Encoder = "copy" },
            Custom = new CustomArguments { Audio = "-disposition:a:0 default" },
        });

        Assert.Equal("copy", After(args, "-c:v"));
        Assert.Equal("copy", After(args, "-c:a"));
        Assert.Contains("-disposition:a:0", args);
        Assert.DoesNotContain("-vf", args);
        Assert.DoesNotContain("-crf", args);
    }

    [Fact]
    public void Unsupported_mode_and_unknown_encoders_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => Single(new EncodePreset { Video = new VideoSettings { Encoder = "libsvtav1", Mode = RateControl.TwoPass } }));
        Assert.Throws<ArgumentException>(() => Single(new EncodePreset { Video = new VideoSettings { Encoder = "nope" } }));
        Assert.Throws<ArgumentException>(() => Single(new EncodePreset { Audio = new AudioSettings { Encoder = "nope" } }));
    }

    [Fact]
    public void Filters_run_in_a_fixed_order()
    {
        IReadOnlyList<string> args = Single(new EncodePreset
        {
            Frame = new FrameSettings
            {
                CropLeft = 2, CropRight = 2, CropTop = 4, CropBottom = 0,
                Deinterlace = Deinterlace.Bwdif,
                Denoise = Denoise.Hqdn3d, DenoiseStrength = 20,
                Deband = true,
                Width = 1280,
                Flip = Flip.Horizontal,
                Rotate = 90,
                Sharpen = 0.5,
            },
            Custom = new CustomArguments { Filter = "eq=gamma=1.1" },
        });

        Assert.Equal(
            "crop=iw-4:ih-4:2:4,bwdif=mode=send_frame,hqdn3d=10,deband,scale=1280:-2:flags=lanczos,hflip,transpose=1,unsharp=5:5:0.5,eq=gamma=1.1",
            After(args, "-vf"));
    }

    [Theory]
    [InlineData(Deinterlace.Yadif, Denoise.Nlmeans, Flip.Vertical, 180, 0, 720, "yadif,nlmeans=s=3,scale=-2:720:flags=lanczos,vflip,hflip,vflip")]
    [InlineData(Deinterlace.None, Denoise.None, Flip.None, 270, 0, 0, "transpose=2")]
    public void Other_filter_choices(Deinterlace deinterlace, Denoise denoise, Flip flip, int rotate, int width, int height, string expected)
    {
        IReadOnlyList<string> args = Single(new EncodePreset
        {
            Frame = new FrameSettings { Deinterlace = deinterlace, Denoise = denoise, Flip = flip, Rotate = rotate, Width = width, Height = height },
        });

        Assert.Equal(expected, After(args, "-vf"));
    }

    [Fact]
    public void Encoder_options_colour_tags_frame_rate_and_keyframes()
    {
        IReadOnlyList<string> args = Single(new EncodePreset
        {
            Video = new VideoSettings
            {
                Encoder = "libx264", Preset = "veryslow", Tune = "animation", Profile = "high10", BitDepth = 10,
                KeyframeInterval = 240, FrameRate = "24000/1001", ConstantFrameRate = false, EncoderParams = "aq-mode=3",
            },
            Color = new ColorSettings { Primaries = "bt709", Transfer = "bt709", Matrix = "bt709", Range = "tv" },
        });

        Assert.Equal("veryslow", After(args, "-preset"));
        Assert.Equal("animation", After(args, "-tune"));
        Assert.Equal("high10", After(args, "-profile:v"));
        Assert.Equal("yuv420p10le", After(args, "-pix_fmt"));
        Assert.Equal("240", After(args, "-g"));
        Assert.Equal("24000/1001", After(args, "-r"));
        Assert.Equal("bt709", After(args, "-color_primaries"));
        Assert.Equal("bt709", After(args, "-color_trc"));
        Assert.Equal("bt709", After(args, "-colorspace"));
        Assert.Equal("tv", After(args, "-color_range"));
        Assert.Equal("aq-mode=3", After(args, "-x264-params"));
        Assert.DoesNotContain("-fps_mode", args);
    }

    [Theory]
    [InlineData("libx265", 8, "yuv420p")]
    [InlineData("libx265", 12, "yuv420p12le")]
    [InlineData("hevc_nvenc", 8, "yuv420p")]
    [InlineData("hevc_nvenc", 10, "p010le")]
    [InlineData("hevc_qsv", 8, "nv12")]
    [InlineData("hevc_qsv", 10, "p010le")]
    public void Bit_depth_picks_the_encoders_pixel_format(string encoder, int depth, string format)
    {
        IReadOnlyList<string> args = Single(new EncodePreset
        {
            Video = new VideoSettings { Encoder = encoder, BitDepth = depth, Mode = encoder == "hevc_amf" ? RateControl.Qp : RateControl.Quality },
        });

        Assert.Equal(format, After(args, "-pix_fmt"));
    }

    [Fact]
    public void Unsupported_depth_warns_and_explicit_pixel_format_wins()
    {
        EncodeCommand command = FfmpegCommandBuilder.Build(
            new EncodePreset { Video = new VideoSettings { Encoder = "h264_nvenc", BitDepth = 10 } }, "/media/in.mkv", Source, "/out/o.mkv", "/tmp");
        Assert.DoesNotContain("-pix_fmt", command.Passes[0]);
        Assert.Single(command.Warnings);

        IReadOnlyList<string> args = Single(new EncodePreset { Video = new VideoSettings { BitDepth = 10, PixelFormat = "yuv444p10le" } });
        Assert.Equal("yuv444p10le", After(args, "-pix_fmt"));
    }

    [Fact]
    public void X264_two_pass_uses_passlogfile()
    {
        EncodeCommand command = FfmpegCommandBuilder.Build(
            new EncodePreset { Video = new VideoSettings { Encoder = "libx264", Mode = RateControl.TwoPass, BitrateKbps = 2500 } },
            "/media/in.mkv", Source, "/out/o.mkv", "/work");

        Assert.Equal(2, command.Passes.Count);
        string log = Assert.Single(command.TemporaryFiles);
        Assert.StartsWith(Path.Combine("/work", "vq-2pass-"), log, StringComparison.Ordinal);
        Assert.Equal("1", After(command.Passes[0], "-pass"));
        Assert.Equal(log, After(command.Passes[0], "-passlogfile"));
        Assert.Contains("-an", command.Passes[0]);
        Assert.Equal(OperatingSystem.IsWindows() ? "NUL" : "/dev/null", command.Passes[0][^1]);
        Assert.Equal("2", After(command.Passes[1], "-pass"));
        Assert.Equal("2500k", After(command.Passes[1], "-b:v"));
        Assert.Equal("/out/o.mkv", command.Passes[1][^1]);
        Assert.Equal(2500, command.VideoBitrateKbps);
    }

    [Fact]
    public void X265_two_pass_goes_through_x265_params_with_the_users_params()
    {
        EncodeCommand command = FfmpegCommandBuilder.Build(
            new EncodePreset { Video = new VideoSettings { Mode = RateControl.TargetSize, TargetMbPer24Minutes = 600, EncoderParams = "aq-mode=3" } },
            "/media/in.mkv", Source, "/out/o.mkv", "/work");

        string log = command.TemporaryFiles.Single().Replace('\\', '/');
        Assert.Equal($"aq-mode=3:pass=1:stats={log}", After(command.Passes[0], "-x265-params"));
        Assert.Equal($"aq-mode=3:pass=2:stats={log}", After(command.Passes[1], "-x265-params"));

        // A drive letter's colon would end the option early.
        EncodeCommand windows = FfmpegCommandBuilder.Build(
            new EncodePreset { Video = new VideoSettings { Mode = RateControl.TwoPass } }, "/media/in.mkv", Source, "/out/o.mkv", @"D:\work");
        Assert.StartsWith(@"pass=1:stats=D\:/work", After(windows.Passes[0], "-x265-params"), StringComparison.Ordinal);
        // 600 MiB per 24 min, 99% of it, minus 2 × 320 kbps of audio.
        Assert.Equal(FfmpegCommandBuilder.TargetVideoKbps(600, TimeSpan.FromMinutes(24), 640), command.VideoBitrateKbps);
        Assert.Equal($"{command.VideoBitrateKbps}k", After(command.Passes[1], "-b:v"));
    }

    [Fact]
    public void Target_size_maths()
    {
        Assert.Equal(0, FfmpegCommandBuilder.TargetVideoKbps(600, TimeSpan.Zero, 0));
        Assert.Equal(3460, FfmpegCommandBuilder.TargetVideoKbps(600, TimeSpan.FromMinutes(24), 0));
        Assert.Throws<ArgumentException>(() => Single(new EncodePreset
        {
            Video = new VideoSettings { Mode = RateControl.TargetSize, TargetMbPer24Minutes = 1 },
        }));
    }

    [Fact]
    public void Target_size_without_two_pass_support_warns()
    {
        EncodeCommand command = FfmpegCommandBuilder.Build(
            new EncodePreset { Video = new VideoSettings { Encoder = "hevc_nvenc", Mode = RateControl.TargetSize } }, "/media/in.mkv", Source, "/out/o.mkv", "/tmp");
        Assert.Single(command.Passes);
        Assert.Empty(command.Warnings);

        command = FfmpegCommandBuilder.Build(
            new EncodePreset { Video = new VideoSettings { Encoder = "hevc_qsv", Mode = RateControl.Bitrate } }, "/media/in.mkv", Source, "/out/o.mkv", "/tmp");
        Assert.Empty(command.Warnings);
    }

    [Fact]
    public void Range_trims_with_seek_and_duration()
    {
        EncodeCommand command = FfmpegCommandBuilder.Build(
            new EncodePreset { Streams = new StreamSettings { Start = 90.5, End = 120 } }, "/media/in.mkv", Source, "/out/o.mkv", "/tmp");
        IReadOnlyList<string> args = command.Passes.Single();

        Assert.Equal("90.5", After(args, "-ss"));
        Assert.Equal("29.5", After(args, "-t"));
        Assert.True(args.ToList().IndexOf("-ss") < args.ToList().IndexOf("-i"));
        Assert.Equal(TimeSpan.FromSeconds(29.5), command.Duration);

        // An end before the start means "to the end".
        Assert.Equal("1430", After(Single(new EncodePreset { Streams = new StreamSettings { Start = 10, End = 5 } }), "-t"));

        args = Single(new EncodePreset { Streams = new StreamSettings { End = 10 } });
        Assert.DoesNotContain("-ss", args);
        Assert.Equal("10", After(args, "-t"));
    }

    [Fact]
    public void Streams_metadata_and_mkv_attachments()
    {
        IReadOnlyList<string> args = Single(new EncodePreset
        {
            Streams = new StreamSettings { KeepSubtitles = true, KeepAttachments = true, KeepChapters = false, KeepMetadata = false, Title = " Ep 5 " },
            Audio = new AudioSettings { Encoder = "aac", FirstTrackOnly = true, Channels = 2, SampleRate = 0 },
            Custom = new CustomArguments { Input = "-hwaccel auto", Video = "-x 1", Output = "-max_muxing_queue_size 4096" },
        });
        string joined = string.Join(' ', args);

        Assert.Contains("-hwaccel auto -i", joined, StringComparison.Ordinal);
        Assert.Contains("-map 0:a:0", joined, StringComparison.Ordinal);
        Assert.Contains("-ac:a:0 2", joined, StringComparison.Ordinal);
        Assert.DoesNotContain("-ar:a:0", joined, StringComparison.Ordinal);
        Assert.Contains("-map 0:s? -c:s copy", joined, StringComparison.Ordinal);
        Assert.Contains("-map 0:t?", joined, StringComparison.Ordinal);
        Assert.Contains("-map_chapters -1 -map_metadata:g -1 -metadata title=Ep 5", joined, StringComparison.Ordinal);
        Assert.Contains("-x 1", joined, StringComparison.Ordinal);
        Assert.Contains("-max_muxing_queue_size 4096 -progress", joined, StringComparison.Ordinal);
    }

    [Fact]
    public void Mp4_gets_faststart_hvc1_and_warnings_for_subtitles_and_fonts()
    {
        EncodeCommand command = FfmpegCommandBuilder.Build(
            new EncodePreset { Container = "MP4", Streams = new StreamSettings { KeepSubtitles = true, KeepAttachments = true } },
            "/media/in.mkv", Source, "/out/o.mp4", "/tmp");
        IReadOnlyList<string> args = command.Passes.Single();

        Assert.Equal("+faststart", After(args, "-movflags"));
        Assert.Equal("hvc1", After(args, "-tag:v"));
        Assert.DoesNotContain("0:s?", args);
        Assert.Equal(2, command.Warnings.Count);

        args = Single(new EncodePreset { Container = "mp4", Video = new VideoSettings { Encoder = "libx264" } });
        Assert.DoesNotContain("-tag:v", args);
    }

    [Fact]
    public void Audio_language_is_written_on_every_track()
    {
        IReadOnlyList<string> args = Single(new EncodePreset { Audio = new AudioSettings { Language = " jpn " } });
        Assert.Equal("language=jpn", After(args, "-metadata:s:a:0"));
        Assert.Equal("language=jpn", After(args, "-metadata:s:a:1"));
        Assert.DoesNotContain("-metadata:s:a:0", Single(new EncodePreset()));
    }

    [Fact]
    public void Chapters_can_come_from_an_ffmetadata_file()
    {
        IReadOnlyList<string> args = Single(new EncodePreset { Streams = new StreamSettings { ChaptersFile = "/w/ch.ffmeta" } });
        Assert.Equal(["-i", "/media/in.mkv", "-f", "ffmetadata", "-i", "/w/ch.ffmeta"], args.Skip(3).Take(6));
        Assert.Equal("1", After(args, "-map_chapters"));
    }

    [Fact]
    public void No_audio_and_lossless_audio()
    {
        Assert.Contains("-an", Single(new EncodePreset { Audio = new AudioSettings { Encoder = "none" } }));
        Assert.Contains("-an", Single(new EncodePreset(), Source with { Streams = [.. Source.Streams.Where(s => s.Kind != StreamKind.Audio)] }));

        IReadOnlyList<string> args = Single(new EncodePreset
        {
            Audio = new AudioSettings { Encoder = "flac", SampleBits = 24, SampleRate = 48000, CopyWhenCompatible = false },
        });
        Assert.Equal("flac", After(args, "-c:a:0"));
        Assert.Equal("s32", After(args, "-sample_fmt:a:0"));
        Assert.Equal("24", After(args, "-bits_per_raw_sample:a:0"));
        Assert.DoesNotContain("-b:a:0", args);

        // A FLAC source track with the same rate is copied when allowed.
        args = Single(new EncodePreset { Audio = new AudioSettings { Encoder = "flac", SampleRate = 48000 } });
        Assert.Equal("copy", After(args, "-c:a:1"));
    }

    [Theory]
    [InlineData(192, 0, 0, 1, "copy")] // any rate and layout
    [InlineData(96, 48000, 2, 1, "aac")] // too high a bitrate
    [InlineData(192, 44100, 2, 1, "aac")] // another sample rate
    [InlineData(192, 48000, 6, 1, "aac")] // another channel count
    [InlineData(192, 48000, 2, 0, "aac")] // unknown bitrate
    public void Audio_is_copied_only_when_it_matches(int kbps, int rate, int channels, int knownBitrate, string expected)
    {
        MediaInfo aac = Source with
        {
            Streams = [.. Source.Streams.Where(s => s.Kind != StreamKind.Audio),
                new MediaStream { Index = 1, Kind = StreamKind.Audio, Codec = "aac", BitRate = 128_000 * knownBitrate, SampleRate = 48000, Channels = 2 }],
        };
        IReadOnlyList<string> args = Single(new EncodePreset { Audio = new AudioSettings { BitrateKbps = kbps, SampleRate = rate, Channels = channels } }, aac);
        Assert.Equal(expected, After(args, "-c:a:0"));
    }

    [Fact]
    public void Burn_in_uses_the_named_file_with_escaped_options()
    {
        IReadOnlyList<string> args = Single(new EncodePreset
        {
            Burn = new SubtitleBurn { File = @"C:\subs\it's [1], a;b.ass", FontsDirectory = "/f o/n:ts", ForceStyle = "Fontsize=24,Bold=1", CharacterEncoding = "GB18030" },
        });

        Assert.Equal(
            @"subtitles=filename=C\\:/subs/it\\\'s \[1\]\, a\;b.ass:fontsdir=/f o/n\\:ts:force_style=Fontsize=24\,Bold=1:charenc=GB18030",
            After(args, "-vf"));
    }

    [Fact]
    public void Burn_in_picks_a_stream_or_a_same_named_script()
    {
        IReadOnlyList<string> args = Single(new EncodePreset { Burn = new SubtitleBurn { StreamIndex = 4 } });
        Assert.Equal("subtitles=filename=/media/in.mkv:si=1", After(args, "-vf"));

        EncodeCommand missing = FfmpegCommandBuilder.Build(new EncodePreset { Burn = new SubtitleBurn { StreamIndex = 9 } }, "/media/in.mkv", Source, "/o.mkv", "/tmp");
        Assert.DoesNotContain("-vf", missing.Passes[0]);
        Assert.Single(missing.Warnings);

        string folder = Path.Combine(Path.GetTempPath(), "vq-burn-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string video = Path.Combine(folder, "ep.mkv");
            EncodeCommand none = FfmpegCommandBuilder.Build(new EncodePreset { Burn = new SubtitleBurn() }, video, Source, "/o.mkv", "/tmp");
            Assert.Single(none.Warnings);

            File.WriteAllText(Path.Combine(folder, "ep.ass"), string.Empty);
            EncodeCommand found = FfmpegCommandBuilder.Build(new EncodePreset { Burn = new SubtitleBurn() }, video, Source, "/o.mkv", "/tmp");
            Assert.Contains("ep.ass", After(found.Passes[0], "-vf"), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void Output_path_follows_the_pattern_and_never_overwrites_the_source()
    {
        Assert.Equal(Path.Combine(Path.GetFullPath("/m"), "ep.encoded.mkv"), FfmpegCommandBuilder.OutputPath(new EncodePreset(), "/m/ep.mp4"));
        Assert.Equal(Path.Combine("/o", "ep.encoded.mp4"), FfmpegCommandBuilder.OutputPath(new EncodePreset { Container = "MP4" }, "/m/ep.mkv", "/o"));
        Assert.Equal(Path.Combine(Path.GetFullPath("/m"), "ep.out.mkv"), FfmpegCommandBuilder.OutputPath(new EncodePreset { OutputPattern = "{name}" }, "/m/ep.mkv"));
    }

    [Fact]
    public void Command_lines_are_quoted_for_display()
    {
        Assert.Equal("ffmpeg -i \"a b.mkv\" -vf \"x\\\\y\\\"z\" -b:v 1k", FfmpegCommandBuilder.FormatCommandLine("ffmpeg", ["-i", "a b.mkv", "-vf", "x\\y\"z", "-b:v", "1k"]));
        Assert.Equal("x \"\"", FfmpegCommandBuilder.FormatCommandLine("x", [string.Empty]));
    }

    [Theory]
    [InlineData(null, new string[0])]
    [InlineData("  ", new string[0])]
    [InlineData("-a 1  -b", new[] { "-a", "1", "-b" })]
    [InlineData("-vf \"scale=1:2,eq\" 'single q' x\"y z\"w", new[] { "-vf", "scale=1:2,eq", "single q", "xy zw" })]
    [InlineData("\"esc \\\" and \\\\ and \\n\" ''", new[] { "esc \" and \\ and \\n", "" })]
    [InlineData("\"tail\\", new[] { "tail\\" })]
    [InlineData("-a ", new[] { "-a" })]
    public void Tokenizer_splits_like_a_shell(string? text, string[] expected) =>
        Assert.Equal(expected, ArgumentTokenizer.Split(text));

    [Theory]
    [InlineData("24000/1001", 24000 / 1001.0)]
    [InlineData(" 25 ", 25.0)]
    [InlineData("30/0", null)]
    [InlineData("1/2/3", null)]
    [InlineData("fast", null)]
    [InlineData("x/2", null)]
    [InlineData(null, null)]
    public void Frame_rates_parse_like_ffmpeg_takes_them(string? text, double? expected) =>
        Assert.Equal(expected, FfmpegCommandBuilder.ParseRate(text));

    [Fact]
    public void Commands_carry_the_output_frame_rate()
    {
        MediaInfo withRate = Source with { Streams = [.. Source.Streams.Select(s => s.Kind == StreamKind.Video ? s with { FrameRate = 23.976 } : s)] };
        Assert.Equal(23.976, FfmpegCommandBuilder.Build(new EncodePreset(), "/media/in.mkv", withRate, "/out/o.mkv", "/tmp").FrameRate);
        Assert.Equal(25, FfmpegCommandBuilder.Build(new EncodePreset { Video = new VideoSettings { FrameRate = "25" } }, "/media/in.mkv", withRate, "/out/o.mkv", "/tmp").FrameRate);
        Assert.Equal(23.976, FfmpegCommandBuilder.Build(new EncodePreset { Video = new VideoSettings { FrameRate = "bogus" } }, "/media/in.mkv", withRate, "/out/o.mkv", "/tmp").FrameRate);
        EncodePreset copy = new() { Video = new VideoSettings { Encoder = "copy", Mode = RateControl.Copy, FrameRate = "25" } };
        Assert.Equal(23.976, FfmpegCommandBuilder.Build(copy, "/media/in.mkv", withRate, "/out/o.mkv", "/tmp").FrameRate); // copied video keeps its rate
        Assert.Equal(0, FfmpegCommandBuilder.Build(new EncodePreset(), "/media/in.mkv", Source with { Streams = [.. Source.Streams.Where(s => s.Kind != StreamKind.Video)] }, "/out/o.mkv", "/tmp").FrameRate);
    }
}
