using System.Globalization;
using System.Text.RegularExpressions;

namespace VideoQuality.Core.Release;

/// <summary>
/// What a source file's name says about the release: title, episode, version, BD or web, movie.
/// Reads the common fan and scene shapes: <c>Title 05.mkv</c>, <c>[Group] Title - 05v2 [1080p].mkv</c>,
/// <c>Title S01E05 1080p WEB-DL.mkv</c>, <c>Title 第05话.mp4</c>, <c>Title OVA 02 [BDRip].mkv</c>.
/// </summary>
public sealed partial record SourceName
{
    public string Title { get; init; } = string.Empty;

    public EpisodeKind Kind { get; init; }

    /// <summary>The episode number; null when the name has none.</summary>
    public int? Episode { get; init; }

    public int Version { get; init; } = 1;

    /// <summary>The name mentions a Blu-ray source (BD, BDRip, Blu-ray, BDMV).</summary>
    public bool Bd { get; init; }

    /// <summary>The name mentions a web source (WEB, WebRip, WEB-DL).</summary>
    public bool Web { get; init; }

    public static SourceName Parse(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        bool bd = BdPattern().IsMatch(name);
        bool web = WebPattern().IsMatch(name);

        // Bracketed groups and tags carry no title; technical tokens are not episode numbers.
        string text = Brackets().Replace(name, " ");
        text = Technical().Replace(text, " ");
        text = text.Replace('_', ' ').Replace('.', ' ');

        EpisodeKind kind = EpisodeKind.Episode;
        int? episode = null;
        int version = 1;
        Match m = new[] { Season(), Special(), Chinese() }.Select(r => r.Match(text)).FirstOrDefault(r => r.Success)
            ?? Number().Matches(text).LastOrDefault()
            ?? Match.Empty;
        if (m.Success)
        {
            episode = int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);
            version = m.Groups["v"].Success ? int.Parse(m.Groups["v"].Value, CultureInfo.InvariantCulture) : 1;
            kind = m.Groups["kind"].Value.ToUpperInvariant() switch
            {
                "OVA" => EpisodeKind.Ova,
                "OAD" => EpisodeKind.Oad,
                "SP" => EpisodeKind.Special,
                _ => EpisodeKind.Episode,
            };
            text = text[..m.Index];
        }

        Match movie = Movie().Match(text);
        if (movie.Success)
        {
            kind = EpisodeKind.Movie;
            episode = null;
            text = text.Remove(movie.Index, movie.Length);
        }

        string title = Spaces().Replace(text, " ").Trim(' ', '-', '~', '+');
        return new SourceName { Title = title, Kind = kind, Episode = episode, Version = version, Bd = bd, Web = web };
    }

    [GeneratedRegex(@"\[[^\]]*\]|【[^】]*】|\([^)]*\)")]
    private static partial Regex Brackets();

    [GeneratedRegex(@"(?i)\b(?:\d{3,4}[pi]|[248]k|x26[45]|h\.?26[45]|hevc|avc|av1|aac|flac|opus|ac3|e-?ac-?3|dts|truehd|ma10p|hi10p|(?:8|10|12)-?bit|web-?dl|web-?rip|web|bd-?rip|bdmv|blu-?ray|bd|remux|dual-?audio|multi-?subs?|cr|amzn|nf|b-?global|baha)\b")]
    private static partial Regex Technical();

    [GeneratedRegex(@"(?i)\b(?:bd|bd-?rip|bdmv|blu-?ray|bdremux)\b")]
    private static partial Regex BdPattern();

    [GeneratedRegex(@"(?i)\bweb(?:-?dl|-?rip)?\b")]
    private static partial Regex WebPattern();

    [GeneratedRegex(@"(?i)\bS\d{1,2}\s*E(?<n>\d{1,4})(?:v(?<v>\d))?")]
    private static partial Regex Season();

    [GeneratedRegex(@"(?i)\b(?<kind>OVA|OAD|SP)\s*-?\s*(?<n>\d{1,3})(?:v(?<v>\d))?\b")]
    private static partial Regex Special();

    [GeneratedRegex(@"第\s*(?<n>\d{1,4})\s*[话話集]")]
    private static partial Regex Chinese();

    /// <summary>Free-standing 1–4 digit numbers (the last one is the episode), optionally with a version: <c>- 05</c>, <c>05v2</c>, <c>E05</c>.</summary>
    [GeneratedRegex(@"(?i)(?<![\w.])(?:EP?\s*)?(?<n>\d{1,4})(?:v(?<v>\d))?(?![\w.])")]
    private static partial Regex Number();

    [GeneratedRegex(@"(?i)\b(?:the\s+)?movie\b|劇場版|剧场版")]
    private static partial Regex Movie();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
