using System.Globalization;
using VideoQuality.Core.Encode;
using VideoQuality.Core.Localization;
using VideoQuality.Core.Media;

namespace VideoQuality.Core.Release;

/// <summary>Source and length class of a release, which decide the encoding targets.</summary>
public enum ReleaseProfile
{
    /// <summary>Web source, TV episode (OVA/OAD/SP follow the same targets).</summary>
    WebEpisode,
    WebMovie,
    BdEpisode,
    BdMovie,
}

public enum FindingLevel
{
    Pass,
    Warning,
    Fail,
}

/// <summary>One line of a compliance report.</summary>
public sealed record SpecFinding(FindingLevel Level, string Message);

/// <summary>Audio target of one release variant.</summary>
public sealed record AudioTarget(string Codec, int BitrateKbps, int SampleRate, int Channels, int SampleBits);

/// <summary>
/// The Studio GreenTea encoding rules (压制规范 v2.1) as data: video and audio targets, size limits,
/// track naming, and a checker that compares a finished file against them.
/// </summary>
public static class GreenTeaSpec
{
    public const int Width = 1920;
    public const int Height = 1080;
    public const double FrameRate = 24000.0 / 1001;

    /// <summary>Track names a subtitle track may carry.</summary>
    public static IReadOnlyList<string> SubtitleNames { get; } = ["SC", "TC", "JPSC", "JPTC"];

    public static bool IsBd(ReleaseProfile profile) => profile is ReleaseProfile.BdEpisode or ReleaseProfile.BdMovie;

    public static bool IsMovie(ReleaseProfile profile) => profile is ReleaseProfile.WebMovie or ReleaseProfile.BdMovie;

    /// <summary>Web: AAC 256k 44.1 kHz stereo. BD 内封: FLAC 24-bit 48 kHz stereo. BD 内嵌: AAC 320k 48 kHz stereo.</summary>
    public static AudioTarget Audio(ReleaseProfile profile, SubtitleDelivery delivery) => (IsBd(profile), delivery) switch
    {
        (false, _) => new AudioTarget("aac", 256, 44100, 2, 0),
        (true, SubtitleDelivery.Embedded) => new AudioTarget("flac", 0, 48000, 2, 24),
        _ => new AudioTarget("aac", 320, 48000, 2, 0),
    };

    /// <summary>The largest allowed file, in MiB, for a release of this length.</summary>
    public static int MaxSizeMb(ReleaseProfile profile, TimeSpan duration)
    {
        double minutes = duration.TotalMinutes;
        return profile switch
        {
            ReleaseProfile.WebMovie => 5 * 1024,
            ReleaseProfile.WebEpisode => minutes switch
            {
                < 20 => 200,
                <= 27 => 600,
                <= 60 => 1024,
                _ => 2 * 1024,
            },
            _ => minutes switch
            {
                < 20 => 400,
                <= 27 => 1100, // "about 1 GB"
                <= 60 => 2 * 1024,
                _ => 6 * 1024,
            },
        };
    }

    /// <summary>An encode preset meeting the rules: x265 10-bit CRF (no GPU encoders), constant 23.976 fps.</summary>
    public static EncodePreset Preset(ReleaseProfile profile, SubtitleDelivery delivery)
    {
        AudioTarget audio = Audio(profile, delivery);
        bool burned = delivery == SubtitleDelivery.Burned;
        string source = IsBd(profile) ? "BD" : "Web";
        return new EncodePreset
        {
            Name = $"GreenTea {source}{(IsMovie(profile) ? " Movie" : string.Empty)} {(burned ? "内嵌 MP4" : "内封 MKV")}",
            Description = Loc.T("spec.preset.description"),
            Container = burned ? "mp4" : "mkv",
            OutputPattern = burned ? "{name}.burned" : "{name}.encoded",
            Video = new VideoSettings
            {
                Encoder = "libx265",
                Mode = RateControl.Quality,
                Quality = IsBd(profile) ? 18 : 20,
                Preset = "slow",
                BitDepth = 10,
                ConstantFrameRate = true,
                FrameRate = "24000/1001",
            },
            Audio = new AudioSettings
            {
                Encoder = audio.Codec,
                BitrateKbps = audio.BitrateKbps,
                SampleRate = audio.SampleRate,
                Channels = audio.Channels,
                SampleBits = audio.SampleBits,
                FirstTrackOnly = true,
                Language = "jpn",
            },
            Burn = burned ? new SubtitleBurn() : null,
            Streams = new StreamSettings { KeepChapters = true, KeepMetadata = false },
            BuiltIn = true,
        };
    }

    /// <summary>The four built-in GreenTea presets (Web/BD × 内封/内嵌).</summary>
    public static IReadOnlyList<EncodePreset> Presets { get; } =
    [
        Preset(ReleaseProfile.WebEpisode, SubtitleDelivery.Embedded),
        Preset(ReleaseProfile.WebEpisode, SubtitleDelivery.Burned),
        Preset(ReleaseProfile.BdEpisode, SubtitleDelivery.Embedded),
        Preset(ReleaseProfile.BdEpisode, SubtitleDelivery.Burned),
    ];

    /// <summary>Checks a finished release file against the rules.</summary>
    public static IReadOnlyList<SpecFinding> Check(MediaInfo info, ReleaseProfile profile, SubtitleDelivery delivery)
    {
        var findings = new List<SpecFinding>();
        void Add(bool ok, string passKey, string failKey, FindingLevel failLevel, params object?[] args) =>
            findings.Add(ok ? new SpecFinding(FindingLevel.Pass, Loc.T(passKey, args)) : new SpecFinding(failLevel, Loc.T(failKey, args)));

        // Name
        ReleaseName? name = ReleaseName.Parse(info.Path);
        Add(name is not null && name.Delivery == delivery, "spec.ok.name", "spec.bad.name", FindingLevel.Fail, Path.GetFileName(info.Path));

        // Video
        if (info.Video is not { } video)
        {
            findings.Add(new SpecFinding(FindingLevel.Fail, Loc.T("spec.bad.noVideo")));
        }
        else
        {
            Add(video.Codec == "hevc" && video.BitDepth == 10, "spec.ok.codec", "spec.bad.codec", FindingLevel.Fail, video.Codec, video.BitDepth);
            Add(video.Width == Width && video.Height == Height, "spec.ok.resolution", "spec.bad.resolution", FindingLevel.Warning, video.Width, video.Height);
            Add(Math.Abs(video.FrameRate - FrameRate) < 0.01, "spec.ok.frameRate", "spec.bad.frameRate", FindingLevel.Warning,
                video.FrameRate.ToString("0.###", CultureInfo.InvariantCulture));
        }

        // Audio
        AudioTarget target = Audio(profile, delivery);
        List<MediaStream> audio = info.AudioStreams.ToList();
        if (audio.Count == 0)
        {
            findings.Add(new SpecFinding(FindingLevel.Fail, Loc.T("spec.bad.noAudio")));
        }

        foreach (MediaStream track in audio)
        {
            bool codec = track.Codec == target.Codec;
            bool rate = track.SampleRate == target.SampleRate;
            bool channels = track.Channels == target.Channels;
            bool bits = target.SampleBits == 0 || track.SampleBits == target.SampleBits;
            Add(codec && rate && channels && bits, "spec.ok.audio", "spec.bad.audio", FindingLevel.Fail,
                track.Index, MediaInfo.Describe(track), Expected(target));

            // The encoder's bitrate is a target: simple audio comes out lower. Above it is a problem.
            if (codec && target.BitrateKbps > 0 && track.BitRate > 0)
            {
                double kbps = track.BitRate / 1000.0;
                if (kbps > target.BitrateKbps * 1.1)
                {
                    findings.Add(new SpecFinding(FindingLevel.Fail, Loc.T("spec.bad.audioBitrateHigh", track.Index, kbps.ToString("0", CultureInfo.InvariantCulture), target.BitrateKbps)));
                }
                else if (kbps < target.BitrateKbps * 0.9)
                {
                    findings.Add(new SpecFinding(FindingLevel.Warning, Loc.T("spec.bad.audioBitrateLow", track.Index, kbps.ToString("0", CultureInfo.InvariantCulture), target.BitrateKbps)));
                }
            }
            Add(IsLanguage(track.Language, "ja"), "spec.ok.audioLanguage", "spec.bad.audioLanguage", FindingLevel.Fail, track.Index, track.Language);
            if (track.Title.Length > 0)
            {
                findings.Add(new SpecFinding(FindingLevel.Warning, Loc.T("spec.bad.audioTitle", track.Index, track.Title)));
            }
        }

        // Subtitles (内封 only; burned-in releases carry none)
        List<MediaStream> subtitles = info.SubtitleStreams.ToList();
        if (delivery == SubtitleDelivery.Embedded)
        {
            Add(subtitles.Count > 0, "spec.ok.subtitles", "spec.bad.noSubtitles", FindingLevel.Fail, subtitles.Count);
            foreach (MediaStream track in subtitles)
            {
                Add(IsLanguage(track.Language, "zh"), "spec.ok.subtitleLanguage", "spec.bad.subtitleLanguage", FindingLevel.Fail, track.Index, track.Language);
                Add(SubtitleNames.Contains(track.Title), "spec.ok.subtitleName", "spec.bad.subtitleName", FindingLevel.Fail, track.Index, track.Title);
            }

            bool fonts = info.Attachments.Any(a => a.MimeType.Contains("font", StringComparison.OrdinalIgnoreCase) ||
                                                   Path.GetExtension(a.FileName).ToLowerInvariant() is ".ttf" or ".otf" or ".ttc");
            Add(fonts || subtitles.Count == 0, "spec.ok.fonts", "spec.bad.fonts", FindingLevel.Warning);
        }
        else if (subtitles.Count > 0)
        {
            findings.Add(new SpecFinding(FindingLevel.Warning, Loc.T("spec.bad.burnedHasSubtitles", subtitles.Count)));
        }

        // Chapters and size
        Add(info.Chapters.Count > 0, "spec.ok.chapters", "spec.bad.chapters", FindingLevel.Fail, info.Chapters.Count);
        int maxMb = MaxSizeMb(profile, info.Duration);
        double sizeMb = info.Size / 1024.0 / 1024.0;
        Add(sizeMb <= maxMb, "spec.ok.size", "spec.bad.size", FindingLevel.Fail,
            sizeMb.ToString("0", CultureInfo.InvariantCulture), maxMb, MediaInfo.FormatClock(info.Duration));
        return findings;
    }

    /// <summary>Matroska stores ISO 639-2 codes (<c>jpn</c>, <c>chi</c>/<c>zho</c>) next to the BCP 47 ones.</summary>
    public static bool IsLanguage(string tag, string bcp47) => (bcp47, tag.ToLowerInvariant()) switch
    {
        ("ja", "ja" or "jpn") => true,
        ("zh", "zh" or "chi" or "zho") => true,
        (_, var t) => t.StartsWith(bcp47 + "-", StringComparison.Ordinal),
    };

    private static string Expected(AudioTarget target) =>
        string.Join(' ', new[]
        {
            target.Codec,
            target.BitrateKbps > 0 ? target.BitrateKbps.ToString(CultureInfo.InvariantCulture) + "k" : string.Empty,
            (target.SampleRate / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + " kHz",
            target.Channels.ToString(CultureInfo.InvariantCulture) + " ch",
            target.SampleBits > 0 ? target.SampleBits.ToString(CultureInfo.InvariantCulture) + "-bit" : string.Empty,
        }.Where(s => s.Length > 0));
}
