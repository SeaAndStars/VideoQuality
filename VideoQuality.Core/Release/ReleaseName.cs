using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace VideoQuality.Core.Release;

/// <summary>What kind of release a file is (decides the episode field).</summary>
public enum EpisodeKind
{
    Episode,
    Movie,
    Ova,
    Oad,
    Special,
}

/// <summary>内封 (soft subtitles, MKV) or 内嵌 (burned in, MP4).</summary>
public enum SubtitleDelivery
{
    Embedded,
    Burned,
}

/// <summary>Which subtitle languages a release carries.</summary>
public enum SubtitleSet
{
    /// <summary>Chinese–Japanese bilingual (JPSC / JPTC, ASSx2).</summary>
    Bilingual,

    /// <summary>Chinese only (SC / TC, SC&amp;TC).</summary>
    Monolingual,
}

/// <summary>Chinese script of a burned-in version.</summary>
public enum ChineseScript
{
    Simplified,
    Traditional,
}

/// <summary>
/// The parts of a release file name, following the Studio GreenTea naming rules:
/// <c>[Group] Title [01][WebRip][HEVC-10bit 1080p AAC ASSx2].mkv</c> (内封) and
/// <c>[Group] Title [01][WebRip][HEVC-10bit 1080p AAC][JPSC].mp4</c> (内嵌).
/// </summary>
public sealed partial record ReleaseName
{
    public const string DefaultGroup = "Studio GreenTea";

    /// <summary>Our group; collaborators come first, joined with <c>&amp;</c>.</summary>
    public string Group { get; init; } = DefaultGroup;

    public IReadOnlyList<string> Collaborators { get; init; } = [];

    /// <summary>Romanised title, e.g. <c>Takopii No Genzai</c>.</summary>
    public string Title { get; init; } = string.Empty;

    public EpisodeKind Kind { get; init; }

    /// <summary>Episode number (ignored for movies).</summary>
    public int Episode { get; init; } = 1;

    /// <summary>Reissue number: 2 writes <c>v2</c>; 1 or less writes nothing.</summary>
    public int Version { get; init; } = 1;

    /// <summary><c>WebRip</c>, <c>BDRip</c>, <c>Web-DL</c>, <c>DVDRip</c>, …</summary>
    public string Source { get; init; } = "WebRip";

    public string VideoCodec { get; init; } = "HEVC-10bit";

    public string Resolution { get; init; } = "1080p";

    public string AudioCodec { get; init; } = "AAC";

    public SubtitleDelivery Delivery { get; init; }

    public SubtitleSet Subtitles { get; init; }

    /// <summary>For a burned-in version: which Chinese script it carries.</summary>
    public ChineseScript Script { get; init; }

    /// <summary>The group tag inside the first brackets.</summary>
    public string GroupTag => string.Join('&', Collaborators.Append(Group).Where(g => g.Length > 0));

    /// <summary><c>01</c>, <c>01v2</c>, <c>Movie</c>, <c>Movie v2</c>, <c>OVA01</c>, <c>OAD01</c>, <c>SP01</c>.</summary>
    public string EpisodeTag
    {
        get
        {
            string number = Episode.ToString("00", CultureInfo.InvariantCulture);
            string version = Version > 1 ? "v" + Version.ToString(CultureInfo.InvariantCulture) : string.Empty;
            return Kind switch
            {
                EpisodeKind.Movie => version.Length > 0 ? "Movie " + version : "Movie",
                EpisodeKind.Ova => "OVA" + number + version,
                EpisodeKind.Oad => "OAD" + number + version,
                EpisodeKind.Special => "SP" + number + version,
                _ => number + version,
            };
        }
    }

    /// <summary><c>ASSx2</c> or <c>SC&amp;TC</c> (内封).</summary>
    public string SubtitleFormatTag => Subtitles == SubtitleSet.Bilingual ? "ASSx2" : "SC&TC";

    /// <summary><c>JPSC</c>, <c>JPTC</c>, <c>SC</c> or <c>TC</c> (内嵌).</summary>
    public string LanguageTag => (Subtitles == SubtitleSet.Bilingual ? "JP" : string.Empty) + (Script == ChineseScript.Simplified ? "SC" : "TC");

    public string Extension => Delivery == SubtitleDelivery.Embedded ? ".mkv" : ".mp4";

    /// <summary>The file name, e.g. <c>[Studio GreenTea] Takopii No Genzai [01][WebRip][HEVC-10bit 1080p AAC ASSx2].mkv</c>.</summary>
    public string FileName
    {
        get
        {
            var name = new StringBuilder();
            name.Append('[').Append(GroupTag).Append("] ").Append(Title.Trim()).Append(" [").Append(EpisodeTag).Append("][")
                .Append(Source).Append("][").Append(VideoCodec).Append(' ').Append(Resolution).Append(' ').Append(AudioCodec);
            name.Append(Delivery == SubtitleDelivery.Embedded ? " " + SubtitleFormatTag + "]" : "][" + LanguageTag + "]");
            return Sanitize(name.Append(Extension).ToString());
        }
    }

    /// <summary>
    /// Release title for the torrent sites: Chinese and romanised names, the main facts in Chinese,
    /// e.g. <c>[绿茶字幕组] 章鱼哔的原罪/Takopii No Genzai [01][WebRip][1080p][繁简日内封]</c>.
    /// </summary>
    public string ReleaseTitle(string chineseGroup, string chineseTitle)
    {
        string names = chineseTitle.Trim().Length > 0 ? chineseTitle.Trim() + "/" + Title.Trim() : Title.Trim();
        string tag = (Delivery, Subtitles, Script) switch
        {
            (SubtitleDelivery.Embedded, SubtitleSet.Bilingual, _) => "繁简日内封",
            (SubtitleDelivery.Embedded, SubtitleSet.Monolingual, _) => "繁简内封",
            (SubtitleDelivery.Burned, SubtitleSet.Bilingual, ChineseScript.Simplified) => "简日内嵌",
            (SubtitleDelivery.Burned, SubtitleSet.Bilingual, ChineseScript.Traditional) => "繁日内嵌",
            (SubtitleDelivery.Burned, SubtitleSet.Monolingual, ChineseScript.Simplified) => "简体内嵌",
            _ => "繁体内嵌",
        };
        return $"[{chineseGroup.Trim()}] {names} [{EpisodeTag}][{Source}][{Resolution}][{tag}]";
    }

    /// <summary>The three versions of a web-source release (内封 MKV plus a simplified and a traditional MP4).</summary>
    public IReadOnlyList<ReleaseName> WebSet() =>
    [
        this with { Delivery = SubtitleDelivery.Embedded },
        this with { Delivery = SubtitleDelivery.Burned, Script = ChineseScript.Simplified },
        this with { Delivery = SubtitleDelivery.Burned, Script = ChineseScript.Traditional },
    ];

    /// <summary>Reads a file name written by <see cref="FileName"/> back; null when it does not follow the rules.</summary>
    public static ReleaseName? Parse(string fileName)
    {
        Match m = Pattern().Match(Path.GetFileName(fileName));
        if (!m.Success)
        {
            return null;
        }

        string[] groups = m.Groups["group"].Value.Split('&');
        string episode = m.Groups["episode"].Value;
        Match e = EpisodePattern().Match(episode);
        EpisodeKind kind = e.Groups["kind"].Value switch
        {
            "Movie" => EpisodeKind.Movie,
            "OVA" => EpisodeKind.Ova,
            "OAD" => EpisodeKind.Oad,
            "SP" => EpisodeKind.Special,
            _ => EpisodeKind.Episode,
        };
        bool burned = m.Groups["lang"].Success;
        string lang = m.Groups["lang"].Value;
        return new ReleaseName
        {
            Group = groups[^1],
            Collaborators = groups[..^1],
            Title = m.Groups["title"].Value,
            Kind = kind,
            Episode = e.Groups["number"].Success ? int.Parse(e.Groups["number"].Value, CultureInfo.InvariantCulture) : 1,
            Version = e.Groups["version"].Success ? int.Parse(e.Groups["version"].Value, CultureInfo.InvariantCulture) : 1,
            Source = m.Groups["source"].Value,
            VideoCodec = m.Groups["video"].Value,
            Resolution = m.Groups["resolution"].Value,
            AudioCodec = m.Groups["audio"].Value,
            Delivery = burned ? SubtitleDelivery.Burned : SubtitleDelivery.Embedded,
            Subtitles = burned ? (lang.StartsWith("JP", StringComparison.Ordinal) ? SubtitleSet.Bilingual : SubtitleSet.Monolingual)
                               : (m.Groups["subs"].Value == "ASSx2" ? SubtitleSet.Bilingual : SubtitleSet.Monolingual),
            Script = lang.EndsWith("TC", StringComparison.Ordinal) ? ChineseScript.Traditional : ChineseScript.Simplified,
        };
    }

    /// <summary>Characters Windows does not allow in file names become full-width look-alikes.</summary>
    public static string Sanitize(string name)
    {
        var text = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            text.Append(c switch
            {
                '/' => '／',
                '\\' => '＼',
                ':' => '：',
                '*' => '＊',
                '?' => '？',
                '"' => '＂',
                '<' => '＜',
                '>' => '＞',
                '|' => '｜',
                _ => c,
            });
        }

        return text.ToString();
    }

    [GeneratedRegex(@"^\[(?<group>[^\]]+)\] (?<title>.+?) \[(?<episode>[^\]]+)\]\[(?<source>[^\]]+)\]\[(?<video>\S+) (?<resolution>\S+) (?<audio>[^\] ]+)(?: (?<subs>ASSx2|SC&TC)\]|\]\[(?<lang>JPSC|JPTC|SC|TC)\])\.(?:mkv|mp4)$")]
    private static partial Regex Pattern();

    [GeneratedRegex(@"^(?:(?<kind>Movie)(?: v(?<version>\d+))?|(?<kind>OVA|OAD|SP)?(?<number>\d+)(?:v(?<version>\d+))?)$")]
    private static partial Regex EpisodePattern();
}
