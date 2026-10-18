using VideoQuality.Core.Encode;
using VideoQuality.Core.Media;
using VideoQuality.Core.Release;

namespace VideoQuality.Tests;

public class ReleaseNameTests
{
    private static readonly ReleaseName Takopii = new() { Title = "Takopii No Genzai", Episode = 1 };

    // Every example of the naming rules (压制规范 v2.1).
    public static TheoryData<ReleaseName, string> Examples => new()
    {
        { Takopii, "[Studio GreenTea] Takopii No Genzai [01][WebRip][HEVC-10bit 1080p AAC ASSx2].mkv" },
        { Takopii with { Delivery = SubtitleDelivery.Burned }, "[Studio GreenTea] Takopii No Genzai [01][WebRip][HEVC-10bit 1080p AAC][JPSC].mp4" },
        { Takopii with { Delivery = SubtitleDelivery.Burned, Script = ChineseScript.Traditional }, "[Studio GreenTea] Takopii No Genzai [01][WebRip][HEVC-10bit 1080p AAC][JPTC].mp4" },
        { new ReleaseName { Title = "Chou Kaguya-hime", Kind = EpisodeKind.Movie }, "[Studio GreenTea] Chou Kaguya-hime [Movie][WebRip][HEVC-10bit 1080p AAC ASSx2].mkv" },
        {
            new ReleaseName { Title = "Silent Witch - Chinmoku no Majo no Kakushigoto", Source = "BDRip", AudioCodec = "FLAC" },
            "[Studio GreenTea] Silent Witch - Chinmoku no Majo no Kakushigoto [01][BDRip][HEVC-10bit 1080p FLAC ASSx2].mkv"
        },
        { Takopii with { Version = 2 }, "[Studio GreenTea] Takopii No Genzai [01v2][WebRip][HEVC-10bit 1080p AAC ASSx2].mkv" },
        {
            new ReleaseName { Title = "Kobayashi-san Chi no Maidragon Samishigariya no Ryuu", Kind = EpisodeKind.Movie, Version = 2, Source = "BDRip", AudioCodec = "FLAC" },
            "[Studio GreenTea] Kobayashi-san Chi no Maidragon Samishigariya no Ryuu [Movie v2][BDRip][HEVC-10bit 1080p FLAC ASSx2].mkv"
        },
        { Takopii with { Subtitles = SubtitleSet.Monolingual }, "[Studio GreenTea] Takopii No Genzai [01][WebRip][HEVC-10bit 1080p AAC SC&TC].mkv" },
        { Takopii with { Subtitles = SubtitleSet.Monolingual, Delivery = SubtitleDelivery.Burned }, "[Studio GreenTea] Takopii No Genzai [01][WebRip][HEVC-10bit 1080p AAC][SC].mp4" },
        { Takopii with { Subtitles = SubtitleSet.Monolingual, Delivery = SubtitleDelivery.Burned, Script = ChineseScript.Traditional }, "[Studio GreenTea] Takopii No Genzai [01][WebRip][HEVC-10bit 1080p AAC][TC].mp4" },
        { Takopii with { Kind = EpisodeKind.Ova }, "[Studio GreenTea] Takopii No Genzai [OVA01][WebRip][HEVC-10bit 1080p AAC ASSx2].mkv" },
        { Takopii with { Kind = EpisodeKind.Oad, Delivery = SubtitleDelivery.Burned }, "[Studio GreenTea] Takopii No Genzai [OAD01][WebRip][HEVC-10bit 1080p AAC][JPSC].mp4" },
        { Takopii with { Kind = EpisodeKind.Special, Episode = 3, Version = 3 }, "[Studio GreenTea] Takopii No Genzai [SP03v3][WebRip][HEVC-10bit 1080p AAC ASSx2].mkv" },
        { Takopii with { Collaborators = ["Partner"] }, "[Partner&Studio GreenTea] Takopii No Genzai [01][WebRip][HEVC-10bit 1080p AAC ASSx2].mkv" },
    };

    [Theory]
    [MemberData(nameof(Examples))]
    public void File_names_follow_the_rules_and_parse_back(ReleaseName name, string expected)
    {
        Assert.Equal(expected, name.FileName);
        ReleaseName parsed = ReleaseName.Parse("/some/folder/" + expected)!;
        Assert.Equal(expected, parsed.FileName);
        Assert.Equal(name.Kind, parsed.Kind);
        Assert.Equal(name.Version, parsed.Version);
        Assert.Equal(name.Collaborators, parsed.Collaborators);
    }

    [Theory]
    [InlineData("random.mkv")]
    [InlineData("[Studio GreenTea] Show [01][WebRip][HEVC-10bit 1080p AAC].mkv")]
    [InlineData("[Studio GreenTea] Show [01][WebRip][HEVC-10bit 1080p AAC][JPSC].avi")]
    public void Other_names_do_not_parse(string name) => Assert.Null(ReleaseName.Parse(name));

    [Fact]
    public void Release_titles_use_chinese_tags()
    {
        Assert.Equal("[绿茶字幕组] 章鱼哔的原罪/Takopii No Genzai [01][WebRip][1080p][繁简日内封]", Takopii.ReleaseTitle("绿茶字幕组", "章鱼哔的原罪"));
        Assert.Equal("[绿茶字幕组] Takopii No Genzai [01][WebRip][1080p][简日内嵌]", (Takopii with { Delivery = SubtitleDelivery.Burned }).ReleaseTitle(" 绿茶字幕组 ", " "));
        Assert.EndsWith("[繁日内嵌]", (Takopii with { Delivery = SubtitleDelivery.Burned, Script = ChineseScript.Traditional }).ReleaseTitle("G", "T"), StringComparison.Ordinal);
        Assert.EndsWith("[繁简内封]", (Takopii with { Subtitles = SubtitleSet.Monolingual }).ReleaseTitle("G", "T"), StringComparison.Ordinal);
        Assert.EndsWith("[简体内嵌]", (Takopii with { Subtitles = SubtitleSet.Monolingual, Delivery = SubtitleDelivery.Burned }).ReleaseTitle("G", "T"), StringComparison.Ordinal);
        Assert.EndsWith("[繁体内嵌]", (Takopii with { Subtitles = SubtitleSet.Monolingual, Delivery = SubtitleDelivery.Burned, Script = ChineseScript.Traditional }).ReleaseTitle("G", "T"), StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_titles_leave_no_double_space_or_dangling_slash()
    {
        ReleaseName untitled = Takopii with { Title = " " };
        Assert.StartsWith("[Studio GreenTea] [01][WebRip]", untitled.FileName, StringComparison.Ordinal);
        Assert.Equal("[绿茶字幕组] 章鱼哔的原罪 [01][WebRip][1080p][繁简日内封]", untitled.ReleaseTitle("绿茶字幕组", "章鱼哔的原罪"));
        Assert.Equal("[绿茶字幕组] [01][WebRip][1080p][繁简日内封]", untitled.ReleaseTitle("绿茶字幕组", string.Empty));
    }

    [Fact]
    public void Web_set_is_the_mkv_and_both_mp4s()
    {
        Assert.Equal(["ASSx2].mkv", "[JPSC].mp4", "[JPTC].mp4"], Takopii.WebSet().Select(n => n.FileName[^10..]).ToArray());
    }

    [Fact]
    public void Unsafe_characters_become_full_width() =>
        Assert.Equal("a／b＼c：d＊e？f＂g＜h＞i｜j", ReleaseName.Sanitize("a/b\\c:d*e?f\"g<h>i|j"));
}

public class GreenTeaSpecTests
{
    private static MediaInfo Release(params MediaStream[] extra) => new()
    {
        Path = "/r/[Studio GreenTea] Takopii No Genzai [01][WebRip][HEVC-10bit 1080p AAC ASSx2].mkv",
        Duration = TimeSpan.FromMinutes(24),
        Size = 400L * 1024 * 1024,
        Chapters = [new MediaChapter(TimeSpan.Zero, TimeSpan.FromMinutes(2), "OP")],
        Streams =
        [
            new MediaStream { Index = 0, Kind = StreamKind.Video, Codec = "hevc", BitDepth = 10, Width = 1920, Height = 1080, FrameRate = 23.976 },
            new MediaStream { Index = 1, Kind = StreamKind.Audio, Codec = "aac", BitRate = 256_000, SampleRate = 44100, Channels = 2, Language = "jpn" },
            new MediaStream { Index = 2, Kind = StreamKind.Subtitle, Codec = "ass", Language = "chi", Title = "JPSC" },
            new MediaStream { Index = 3, Kind = StreamKind.Subtitle, Codec = "ass", Language = "zh", Title = "JPTC" },
            new MediaStream { Index = 4, Kind = StreamKind.Attachment, FileName = "ABCDEFGH.ttf", MimeType = "font/ttf" },
            .. extra,
        ],
    };

    [Fact]
    public void A_correct_web_release_passes_everything()
    {
        IReadOnlyList<SpecFinding> findings = GreenTeaSpec.Check(Release(), ReleaseProfile.WebEpisode, SubtitleDelivery.Embedded);
        Assert.All(findings, f => Assert.Equal(FindingLevel.Pass, f.Level));
    }

    [Fact]
    public void Every_rule_reports_its_violation()
    {
        MediaInfo bad = new()
        {
            Path = "/r/whatever.mkv",
            Duration = TimeSpan.FromMinutes(24),
            Size = 900L * 1024 * 1024,
            Streams =
            [
                new MediaStream { Index = 0, Kind = StreamKind.Video, Codec = "h264", BitDepth = 8, Width = 1280, Height = 720, FrameRate = 29.97 },
                new MediaStream { Index = 1, Kind = StreamKind.Audio, Codec = "aac", BitRate = 128_000, SampleRate = 48000, Channels = 2, Language = "eng", Title = "jpn" },
                new MediaStream { Index = 2, Kind = StreamKind.Subtitle, Codec = "ass", Language = "en", Title = "English" },
            ],
        };

        IReadOnlyList<SpecFinding> findings = GreenTeaSpec.Check(bad, ReleaseProfile.WebEpisode, SubtitleDelivery.Embedded);

        Assert.Single(findings, f => f.Level == FindingLevel.Pass); // it does carry a subtitle track
        Assert.Equal(5, findings.Count(f => f.Level == FindingLevel.Warning)); // resolution, frame rate, audio title, low bitrate, fonts
        Assert.Equal(8, findings.Count(f => f.Level == FindingLevel.Fail));
    }

    [Fact]
    public void Missing_tracks_and_burned_versions()
    {
        IReadOnlyList<SpecFinding> empty = GreenTeaSpec.Check(new MediaInfo { Path = "x.mkv" }, ReleaseProfile.BdMovie, SubtitleDelivery.Embedded);
        Assert.Contains(empty, f => f.Level == FindingLevel.Fail && f.Message.Contains("video", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(empty, f => f.Level == FindingLevel.Fail && f.Message.Contains("audio", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(empty, f => f.Level == FindingLevel.Pass && f.Message.Contains("ont", StringComparison.Ordinal)); // no subtitles: no fonts needed

        MediaInfo burned = Release() with { Path = "/r/[Studio GreenTea] Takopii No Genzai [01][WebRip][HEVC-10bit 1080p AAC][JPSC].mp4" };
        IReadOnlyList<SpecFinding> findings = GreenTeaSpec.Check(burned, ReleaseProfile.WebEpisode, SubtitleDelivery.Burned);
        Assert.Single(findings, f => f.Level == FindingLevel.Warning); // still carries subtitle tracks
        Assert.Contains(findings, f => f.Message.Contains("JPSC", StringComparison.Ordinal) && f.Level == FindingLevel.Pass);

        MediaInfo clean = burned with { Streams = [.. burned.Streams.Where(st => st.Kind != StreamKind.Subtitle)] };
        Assert.DoesNotContain(GreenTeaSpec.Check(clean, ReleaseProfile.WebEpisode, SubtitleDelivery.Burned), f => f.Level == FindingLevel.Warning);
    }

    [Fact]
    public void Bd_audio_targets_are_checked()
    {
        MediaInfo flac = Release() with
        {
            Streams = [.. Release().Streams.Select(s => s.Kind == StreamKind.Audio ? s with { Codec = "flac", BitRate = 0, SampleRate = 48000, SampleBits = 24 } : s)],
        };
        Assert.DoesNotContain(GreenTeaSpec.Check(flac, ReleaseProfile.BdEpisode, SubtitleDelivery.Embedded), f => f.Level == FindingLevel.Fail && f.Message.Contains("#1", StringComparison.Ordinal));

        MediaInfo sixteen = flac with { Streams = [.. flac.Streams.Select(s => s.Kind == StreamKind.Audio ? s with { SampleBits = 16 } : s)] };
        Assert.Contains(GreenTeaSpec.Check(sixteen, ReleaseProfile.BdEpisode, SubtitleDelivery.Embedded), f => f.Level == FindingLevel.Fail && f.Message.Contains("24-bit", StringComparison.Ordinal));
    }

    [Fact]
    public void Audio_above_the_target_bitrate_fails()
    {
        MediaInfo loud = Release() with
        {
            Streams = [.. Release().Streams.Select(s => s.Kind == StreamKind.Audio ? s with { BitRate = 400_000 } : s)],
        };
        Assert.Contains(GreenTeaSpec.Check(loud, ReleaseProfile.WebEpisode, SubtitleDelivery.Embedded),
            f => f.Level == FindingLevel.Fail && f.Message.Contains("400", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(ReleaseProfile.WebEpisode, 12, 200)]
    [InlineData(ReleaseProfile.WebEpisode, 24, 600)]
    [InlineData(ReleaseProfile.WebEpisode, 45, 1024)]
    [InlineData(ReleaseProfile.WebEpisode, 90, 2048)]
    [InlineData(ReleaseProfile.WebMovie, 120, 5120)]
    [InlineData(ReleaseProfile.BdEpisode, 12, 400)]
    [InlineData(ReleaseProfile.BdEpisode, 24, 1100)]
    [InlineData(ReleaseProfile.BdEpisode, 45, 2048)]
    [InlineData(ReleaseProfile.BdMovie, 120, 6144)]
    public void Size_limits_depend_on_source_and_length(ReleaseProfile profile, int minutes, int megabytes) =>
        Assert.Equal(megabytes, GreenTeaSpec.MaxSizeMb(profile, TimeSpan.FromMinutes(minutes)));

    [Fact]
    public void Presets_follow_the_audio_and_video_rules()
    {
        Assert.Equal(4, GreenTeaSpec.Presets.Count);
        EncodePreset bdBurned = GreenTeaSpec.Preset(ReleaseProfile.BdMovie, SubtitleDelivery.Burned);
        Assert.Equal("mp4", bdBurned.Container);
        Assert.NotNull(bdBurned.Burn);
        Assert.Equal(("aac", 320, 48000), (bdBurned.Audio.Encoder, bdBurned.Audio.BitrateKbps, bdBurned.Audio.SampleRate));
        Assert.Contains("Movie", bdBurned.Name, StringComparison.Ordinal);
        EncodePreset bd = GreenTeaSpec.Preset(ReleaseProfile.BdEpisode, SubtitleDelivery.Embedded);
        Assert.Equal(("flac", 24), (bd.Audio.Encoder, bd.Audio.SampleBits));
        Assert.All(GreenTeaSpec.Presets, p => Assert.Equal(("libx265", RateControl.Quality, 10), (p.Video.Encoder, p.Video.Mode, p.Video.BitDepth)));
    }

    [Theory]
    [InlineData("jpn", "ja", true)]
    [InlineData("ja", "ja", true)]
    [InlineData("zho", "zh", true)]
    [InlineData("zh-Hant", "zh", true)]
    [InlineData("und", "zh", false)]
    [InlineData("", "ja", false)]
    public void Language_tags_match_both_code_styles(string tag, string language, bool expected) =>
        Assert.Equal(expected, GreenTeaSpec.IsLanguage(tag, language));
}

public class PresetStoreTests
{
    [Fact]
    public void Save_load_and_delete_user_presets()
    {
        string folder = Path.Combine(Path.GetTempPath(), "vq-presets-" + Guid.NewGuid().ToString("N"));
        var store = new PresetStore(folder);
        Assert.Equal(PresetStore.BuiltIn.Count, store.All().Count);
        Assert.EndsWith("presets", PresetStore.DefaultDirectory, StringComparison.Ordinal);

        EncodePreset mine = store.Save(new EncodePreset { Name = "Mine: fast?", Burn = new SubtitleBurn { File = "a.ass" }, Video = new VideoSettings { Mode = RateControl.TwoPass } });
        EncodePreset copy = store.Save(PresetStore.BuiltIn[0]);
        EncodePreset unnamed = store.Save(new EncodePreset { Name = " " });
        File.WriteAllText(Path.Combine(folder, "broken.json"), "{ not json");

        IReadOnlyList<EncodePreset> all = store.All();
        Assert.Equal(PresetStore.BuiltIn.Count + 3, all.Count);
        Assert.Contains(all, p => p.Name == "Mine: fast?" && p.Burn?.File == "a.ass" && p.Video.Mode == RateControl.TwoPass && !p.BuiltIn);
        Assert.EndsWith(" (copy)", copy.Name, StringComparison.Ordinal);
        Assert.Equal("Preset (copy)", unnamed.Name);
        Assert.True(File.Exists(Path.Combine(folder, "Mine： fast？.json")));
        Assert.True(store.Delete(mine.Name));
        Assert.False(store.Delete(mine.Name));
        Assert.Throws<System.Text.Json.JsonException>(() => PresetStore.Read("null"));
        Assert.Contains("\"mode\": \"TwoPass\"", PresetStore.Write(mine), StringComparison.Ordinal);
        Directory.Delete(folder, true);
    }
}
