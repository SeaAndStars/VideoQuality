using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using VideoQuality.Core.Localization;
using VideoQuality.Core.Media;

namespace VideoQuality.Core.Chapters;

/// <summary>A chapter mark: where it starts and what it is called.</summary>
public sealed record ChapterMark(TimeSpan Start, string Name);

/// <summary>
/// Chapter files: read and write the simple OGM text format and Matroska XML (both accepted by
/// mkvmerge), import a video's chapters, name them the fansub way and check them.
/// </summary>
public static partial class ChapterList
{
    /// <summary>Reads OGM (<c>CHAPTER01=…</c>) or Matroska XML, by content.</summary>
    public static IReadOnlyList<ChapterMark> Parse(string text) =>
        text.TrimStart('﻿', ' ', '\r', '\n', '\t').StartsWith('<') ? ParseXml(text) : ParseOgm(text);

    public static IReadOnlyList<ChapterMark> Load(string path) => Parse(File.ReadAllText(path));

    public static IReadOnlyList<ChapterMark> FromMedia(MediaInfo info) =>
        info.Chapters.Select(c => new ChapterMark(c.Start, c.Title)).ToList();

    public static IReadOnlyList<ChapterMark> ParseOgm(string text)
    {
        var times = new SortedDictionary<int, TimeSpan>();
        var names = new Dictionary<int, string>();
        foreach (Match m in OgmLine().Matches(text))
        {
            int number = int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);
            string value = m.Groups["value"].Value.Trim();
            if (m.Groups["name"].Success)
            {
                names[number] = value;
            }
            else if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out TimeSpan time))
            {
                times[number] = time;
            }
        }

        return times.Select(t => new ChapterMark(t.Value, names.GetValueOrDefault(t.Key, string.Empty))).ToList();
    }

    public static IReadOnlyList<ChapterMark> ParseXml(string text)
    {
        // Files saved by Windows tools often start with a BOM or blank lines, which XML rejects.
        XDocument document = XDocument.Parse(text.TrimStart('\uFEFF', ' ', '\r', '\n', '\t'));
        return document.Descendants("ChapterAtom")
            .Select(atom => new ChapterMark(
                ParseTime((string?)atom.Element("ChapterTimeStart") ?? "00:00:00"),
                (string?)atom.Element("ChapterDisplay")?.Element("ChapterString") ?? string.Empty))
            .OrderBy(c => c.Start)
            .ToList();
    }

    public static string ToOgm(IReadOnlyList<ChapterMark> chapters)
    {
        var text = new StringBuilder();
        for (int i = 0; i < chapters.Count; i++)
        {
            string n = (i + 1).ToString("00", CultureInfo.InvariantCulture);
            text.Append("CHAPTER").Append(n).Append('=').AppendLine(FormatTime(chapters[i].Start));
            text.Append("CHAPTER").Append(n).Append("NAME=").AppendLine(chapters[i].Name);
        }

        return text.ToString();
    }

    /// <param name="language">ISO 639-2 language of the names (<c>jpn</c>, <c>chi</c>, <c>eng</c>).</param>
    public static string ToXml(IReadOnlyList<ChapterMark> chapters, string language = "und")
    {
        var edition = new XElement("EditionEntry");
        foreach (ChapterMark chapter in chapters)
        {
            edition.Add(new XElement("ChapterAtom",
                new XElement("ChapterTimeStart", FormatTime(chapter.Start) + "000000"),
                new XElement("ChapterDisplay",
                    new XElement("ChapterString", chapter.Name),
                    new XElement("ChapterLanguage", language))));
        }

        var declaration = new XDeclaration("1.0", "utf-8", null);
        var document = new XDocument(declaration, new XDocumentType("Chapters", null, "matroskachapters.dtd", null), new XElement("Chapters", edition));
        return declaration.ToString() + Environment.NewLine + document.ToString();
    }

    /// <summary>
    /// ffmpeg's metadata format (for adding chapters while encoding, e.g. to an MP4). Each chapter ends
    /// where the next starts; the last at <paramref name="duration"/>.
    /// </summary>
    public static string ToFfMetadata(IReadOnlyList<ChapterMark> chapters, TimeSpan duration)
    {
        var text = new StringBuilder(";FFMETADATA1\n");
        for (int i = 0; i < chapters.Count; i++)
        {
            long start = (long)chapters[i].Start.TotalMilliseconds;
            long end = (long)(i + 1 < chapters.Count ? chapters[i + 1].Start : Max(duration, chapters[i].Start)).TotalMilliseconds;
            text.Append("[CHAPTER]\nTIMEBASE=1/1000\n")
                .Append("START=").Append(start.ToString(CultureInfo.InvariantCulture)).Append('\n')
                .Append("END=").Append(end.ToString(CultureInfo.InvariantCulture)).Append('\n')
                .Append("title=").Append(EscapeMetadata(chapters[i].Name)).Append('\n');
        }

        return text.ToString();
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    // ffmetadata: '=', ';', '#', '\\' and newlines are escaped with a backslash.
    private static string EscapeMetadata(string value)
    {
        var escaped = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            if (c is '=' or ';' or '#' or '\\' or '\n')
            {
                escaped.Append('\\');
            }

            escaped.Append(c);
        }

        return escaped.ToString();
    }

    /// <summary>Writes Matroska XML for <c>.xml</c> paths and OGM text otherwise (UTF-8).</summary>
    public static void Save(string path, IReadOnlyList<ChapterMark> chapters) =>
        File.WriteAllText(path, path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ? ToXml(chapters) : ToOgm(chapters), new UTF8Encoding(false));

    /// <summary>
    /// Names marks the fansub way: the marks listed as opening/ending become <c>OP</c>/<c>ED</c>,
    /// the rest <c>Chapter1</c>, <c>Chapter2</c>, … in order.
    /// </summary>
    public static IReadOnlyList<ChapterMark> AutoName(IReadOnlyList<ChapterMark> chapters, int? opening, int? ending)
    {
        int next = 1;
        return chapters.Select((c, i) => c with
        {
            Name = i == opening ? "OP" : i == ending ? "ED" : "Chapter" + (next++).ToString(CultureInfo.InvariantCulture),
        }).ToList();
    }

    /// <summary>Problems worth fixing before muxing (empty when the list is fine).</summary>
    public static IReadOnlyList<string> Check(IReadOnlyList<ChapterMark> chapters, TimeSpan duration)
    {
        var problems = new List<string>();
        if (chapters.Count == 0)
        {
            problems.Add(Loc.T("chapters.check.empty"));
            return problems;
        }

        if (chapters[0].Start > TimeSpan.FromSeconds(1))
        {
            problems.Add(Loc.T("chapters.check.firstNotZero", FormatTime(chapters[0].Start)));
        }

        for (int i = 1; i < chapters.Count; i++)
        {
            if (chapters[i].Start <= chapters[i - 1].Start)
            {
                problems.Add(Loc.T("chapters.check.order", i + 1));
            }
        }

        if (duration > TimeSpan.Zero && chapters[^1].Start > duration)
        {
            problems.Add(Loc.T("chapters.check.pastEnd", FormatTime(chapters[^1].Start), FormatTime(duration)));
        }

        int unnamed = chapters.Count(c => string.IsNullOrWhiteSpace(c.Name));
        if (unnamed > 0)
        {
            problems.Add(Loc.T("chapters.check.unnamed", unnamed));
        }

        return problems;
    }

    public static string FormatTime(TimeSpan time) => time.ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture);

    /// <summary>Parses <c>hh:mm:ss.fffffffff</c> (Matroska keeps nanoseconds) or <c>hh:mm:ss.fff</c>.</summary>
    public static TimeSpan ParseTime(string text)
    {
        Match m = TimePattern().Match(text.Trim());
        if (!m.Success)
        {
            throw new FormatException(Loc.T("chapters.error.time", text));
        }

        string fraction = (m.Groups["f"].Value + "0000000")[..7];
        return new TimeSpan(
            0,
            int.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture),
            int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture),
            int.Parse(m.Groups["s"].Value, CultureInfo.InvariantCulture))
            + TimeSpan.FromTicks(long.Parse(fraction, CultureInfo.InvariantCulture));
    }

    [GeneratedRegex(@"^\s*CHAPTER(?<n>\d+)(?<name>NAME)?=(?<value>.*?)\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex OgmLine();

    [GeneratedRegex(@"^(?<h>\d+):(?<m>\d{1,2}):(?<s>\d{1,2})(?:\.(?<f>\d+))?$")]
    private static partial Regex TimePattern();
}
