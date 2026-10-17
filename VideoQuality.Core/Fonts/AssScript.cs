using System.Globalization;
using System.Text;

namespace VideoQuality.Core.Fonts;

/// <summary>A font as an ASS renderer asks for it: family name, weight and slant.</summary>
/// <param name="Family">Name as written in the script, without the vertical-text '@' prefix.</param>
/// <param name="Weight">400 regular, 700 bold, or an explicit \b weight.</param>
/// <param name="Italic">Whether italic was requested.</param>
public readonly record struct FontRequest(string Family, int Weight, bool Italic)
{
    public bool IsBold => Weight >= 600;

    public override string ToString() =>
        Family + (IsBold ? " Bold" : string.Empty) + (Italic ? " Italic" : string.Empty);
}

/// <summary>
/// An ASS/SSA script: enough structure to find every font and character it renders, and to rename
/// fonts without touching anything else (line endings, encoding and unknown sections round-trip).
/// </summary>
public sealed class AssScript
{
    private readonly List<string> _lines;
    private readonly string _newline;
    private readonly byte[] _bom;

    private AssScript(List<string> lines, string newline, Encoding encoding, byte[] bom)
    {
        _lines = lines;
        _newline = newline;
        _bom = bom;
        Encoding = encoding;
    }

    public Encoding Encoding { get; }

    public bool HadBom => _bom.Length > 0;

    public static AssScript Load(string path) => Parse(File.ReadAllBytes(path));

    /// <summary>
    /// Decodes with the BOM when there is one, strict UTF-8 otherwise, and GB18030 for legacy
    /// Chinese scripts that are not valid UTF-8.
    /// </summary>
    public static AssScript Parse(byte[] bytes)
    {
        (Encoding encoding, int skip) = DetectEncoding(bytes);
        string text = encoding.GetString(bytes, skip, bytes.Length - skip);
        string newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        return new AssScript(lines, newline, encoding, bytes[..skip]);
    }

    private static (Encoding Encoding, int Skip) DetectEncoding(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return (new UTF8Encoding(false), 3);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return (new UnicodeEncoding(false, false), 2);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return (new UnicodeEncoding(true, false), 2);
        }

        try
        {
            _ = new UTF8Encoding(false, throwOnInvalidBytes: true).GetCharCount(bytes);
            return (new UTF8Encoding(false), 0);
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return (Encoding.GetEncoding("GB18030"), 0);
        }
    }

    /// <summary>Font names in style definitions (whether or not any line draws with them).</summary>
    public IReadOnlyCollection<string> StyleFontNames() =>
        ReadStyles().Values.Select(s => s.Family.TrimStart('@').Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every font the script renders, with the characters drawn in it.</summary>
    public Dictionary<FontRequest, HashSet<int>> CollectGlyphs()
    {
        var result = new Dictionary<FontRequest, HashSet<int>>();
        Dictionary<string, StyleFont> styles = ReadStyles();
        StyleFont fallback = styles.Values.FirstOrDefault() ?? new StyleFont("Arial", 400, false);

        foreach ((string style, string text) in ReadDialogue())
        {
            StyleFont lineStyle = styles.TryGetValue(style.TrimStart('*'), out StyleFont? found) ? found : fallback;
            CollectLine(text, lineStyle, styles, result);
        }

        return result;
    }

    /// <summary>
    /// Renames fonts in style definitions and \fn tags. Keys are names as written in the script
    /// (case-insensitive, without '@'); a vertical '@' prefix is preserved.
    /// </summary>
    public void RenameFonts(IReadOnlyDictionary<string, string> renames, IEnumerable<string>? comments = null)
    {
        var map = new Dictionary<string, string>(renames, StringComparer.OrdinalIgnoreCase);
        string? section = null;
        int[]? styleFormat = null;
        int styleFontIndex = -1;

        for (int i = 0; i < _lines.Count; i++)
        {
            string line = _lines[i];
            string trimmed = line.Trim();

            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                section = trimmed.ToLowerInvariant();
                continue;
            }

            if (IsStyleSection(section))
            {
                if (TrySplitKey(line, "Format", out string? format))
                {
                    string[] fields = SplitFields(format, int.MaxValue);
                    styleFontIndex = Array.FindIndex(fields, f => f.Trim().Equals("Fontname", StringComparison.OrdinalIgnoreCase));
                    styleFormat = [fields.Length];
                }
                else if (styleFontIndex >= 0 && TrySplitKey(line, "Style", out string? value))
                {
                    string[] fields = SplitFields(value, styleFormat![0]);
                    if (styleFontIndex < fields.Length)
                    {
                        fields[styleFontIndex] = RenameOne(fields[styleFontIndex], map);
                        _lines[i] = line[..(line.IndexOf(':') + 1)] + " " + string.Join(",", fields).TrimStart();
                    }
                }
            }
            else if (IsEventsSection(section) && (line.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase) ||
                                                    line.StartsWith("Comment:", StringComparison.OrdinalIgnoreCase)))
            {
                _lines[i] = RenameFnTags(line, map);
            }
        }

        if (comments is not null)
        {
            int scriptInfo = _lines.FindIndex(l => l.Trim().Equals("[Script Info]", StringComparison.OrdinalIgnoreCase));
            int insertAt = scriptInfo >= 0 ? scriptInfo + 1 : 0;
            _lines.InsertRange(insertAt, comments.Select(c => "; " + c));
        }
    }

    /// <summary>The script in its original encoding, with the original byte-order mark (if any).</summary>
    public byte[] ToBytes() => [.. _bom, .. Encoding.GetBytes(string.Join(_newline, _lines))];

    public void Save(string path) => File.WriteAllBytes(path, ToBytes());

    // ---------------------------------------------------------------- styles and events

    private sealed record StyleFont(string Family, int Weight, bool Italic);

    private static bool IsStyleSection(string? section) => section is "[v4+ styles]" or "[v4 styles]" or "[v4 styles+]";

    private static bool IsEventsSection(string? section) => section == "[events]";

    private Dictionary<string, StyleFont> ReadStyles()
    {
        var styles = new Dictionary<string, StyleFont>(StringComparer.OrdinalIgnoreCase);
        string? section = null;
        string[]? format = null;

        foreach (string line in _lines)
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                section = trimmed.ToLowerInvariant();
                continue;
            }

            if (!IsStyleSection(section))
            {
                continue;
            }

            if (TrySplitKey(line, "Format", out string? formatText))
            {
                format = SplitFields(formatText, int.MaxValue).Select(f => f.Trim().ToLowerInvariant()).ToArray();
            }
            else if (format is not null && TrySplitKey(line, "Style", out string? value))
            {
                string[] fields = SplitFields(value, format.Length);
                string Field(string name) => Array.IndexOf(format, name) is int index and >= 0 && index < fields.Length
                    ? fields[index].Trim()
                    : string.Empty;

                string name = Field("name");
                string font = Field("fontname");
                if (name.Length > 0 && font.Length > 0)
                {
                    styles[name] = new StyleFont(font, ParseStyleBold(Field("bold")), ParseFlag(Field("italic")));
                }
            }
        }

        return styles;
    }

    private IEnumerable<(string Style, string Text)> ReadDialogue()
    {
        string? section = null;
        string[]? format = null;

        foreach (string line in _lines)
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                section = trimmed.ToLowerInvariant();
                continue;
            }

            if (!IsEventsSection(section))
            {
                continue;
            }

            if (TrySplitKey(line, "Format", out string? formatText))
            {
                format = SplitFields(formatText, int.MaxValue).Select(f => f.Trim().ToLowerInvariant()).ToArray();
            }
            else if (format is not null && TrySplitKey(line, "Dialogue", out string? value))
            {
                // Text is the last field and may itself contain commas.
                string[] fields = SplitFields(value, format.Length);
                int styleIndex = Array.IndexOf(format, "style");
                int textIndex = Array.IndexOf(format, "text");
                if (textIndex >= 0 && textIndex < fields.Length)
                {
                    string style = styleIndex >= 0 && styleIndex < fields.Length ? fields[styleIndex].Trim() : string.Empty;
                    yield return (style, fields[textIndex]);
                }
            }
        }
    }

    // ---------------------------------------------------------------- override tags

    /// <summary>
    /// Walks one line's text with the renderer's state machine: override blocks change the font,
    /// weight, slant or drawing mode for the characters that follow.
    /// </summary>
    private static void CollectLine(
        string text,
        StyleFont lineStyle,
        Dictionary<string, StyleFont> styles,
        Dictionary<FontRequest, HashSet<int>> result)
    {
        string family = lineStyle.Family;
        int weight = lineStyle.Weight;
        bool italic = lineStyle.Italic;
        bool drawing = false;

        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];

            if (c == '{')
            {
                int end = text.IndexOf('}', i + 1);
                if (end < 0)
                {
                    break; // unterminated block: VSFilter drops the rest of the line
                }

                foreach (string tag in text[(i + 1)..end].Split('\\', StringSplitOptions.RemoveEmptyEntries))
                {
                    ApplyTag(tag.Trim(), lineStyle, styles, ref family, ref weight, ref italic, ref drawing);
                }

                i = end + 1;
                continue;
            }

            if (c == '\\' && i + 1 < text.Length)
            {
                char next = text[i + 1];
                if (next is 'N' or 'n')
                {
                    i += 2; // line break, not a glyph
                    continue;
                }

                if (next == 'h')
                {
                    Add(result, family, weight, italic, 0x00A0);
                    i += 2;
                    continue;
                }
            }

            if (drawing)
            {
                i++;
                continue;
            }

            // Decoding replaced any unpaired surrogate, so a high surrogate always has its pair.
            int codepoint = char.ConvertToUtf32(text, i);
            Add(result, family, weight, italic, codepoint);
            i += codepoint > 0xFFFF ? 2 : 1;
        }
    }

    private static void ApplyTag(
        string tag,
        StyleFont lineStyle,
        Dictionary<string, StyleFont> styles,
        ref string family,
        ref int weight,
        ref bool italic,
        ref bool drawing)
    {
        if (tag.StartsWith("fn", StringComparison.Ordinal))
        {
            string name = tag[2..].Trim();
            family = name.Length > 0 ? name : lineStyle.Family;
        }
        else if (tag.StartsWith('r') && !tag.StartsWith("rnd", StringComparison.Ordinal))
        {
            string name = tag[1..].Trim();
            StyleFont reset = name.Length > 0 && styles.TryGetValue(name, out StyleFont? named) ? named : lineStyle;
            (family, weight, italic) = (reset.Family, reset.Weight, reset.Italic);
        }
        else if (tag.StartsWith('b') && !tag.StartsWith("blur", StringComparison.Ordinal) &&
                 !tag.StartsWith("bord", StringComparison.Ordinal) && !tag.StartsWith("be", StringComparison.Ordinal))
        {
            string value = tag[1..].Trim();
            weight = value.Length == 0 ? lineStyle.Weight : ParseOverrideBold(value, lineStyle.Weight);
        }
        else if (tag.StartsWith('i') && !tag.StartsWith("iclip", StringComparison.Ordinal))
        {
            string value = tag[1..].Trim();
            italic = value.Length == 0 ? lineStyle.Italic : value != "0";
        }
        else if (tag.StartsWith('p') && !tag.StartsWith("pos", StringComparison.Ordinal) &&
                 !tag.StartsWith("pbo", StringComparison.Ordinal))
        {
            drawing = int.TryParse(tag[1..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int scale) && scale > 0;
        }
    }

    private static void Add(Dictionary<FontRequest, HashSet<int>> result, string family, int weight, bool italic, int codepoint)
    {
        var request = new FontRequest(family.TrimStart('@').Trim(), weight, italic);
        if (!result.TryGetValue(request, out HashSet<int>? set))
        {
            result[request] = set = [];
        }

        set.Add(codepoint);
    }

    private static string RenameFnTags(string line, Dictionary<string, string> map)
    {
        var sb = new StringBuilder(line.Length);
        int i = 0;
        while (i < line.Length)
        {
            int open = line.IndexOf('{', i);
            if (open < 0)
            {
                sb.Append(line, i, line.Length - i);
                break;
            }

            int close = line.IndexOf('}', open + 1);
            if (close < 0)
            {
                sb.Append(line, i, line.Length - i);
                break;
            }

            sb.Append(line, i, open - i);
            string block = line[(open + 1)..close];
            string[] parts = block.Split('\\');
            for (int p = 0; p < parts.Length; p++)
            {
                if (parts[p].StartsWith("fn", StringComparison.Ordinal) && parts[p].Length > 2)
                {
                    parts[p] = "fn" + RenameOne(parts[p][2..], map);
                }
            }

            sb.Append('{').Append(string.Join('\\', parts)).Append('}');
            i = close + 1;
        }

        return sb.ToString();
    }

    private static string RenameOne(string name, Dictionary<string, string> map)
    {
        string trimmed = name.Trim();
        bool vertical = trimmed.StartsWith('@');
        string key = trimmed.TrimStart('@');
        return map.TryGetValue(key, out string? renamed) ? (vertical ? "@" : string.Empty) + renamed : name;
    }

    // ---------------------------------------------------------------- field helpers

    private static bool TrySplitKey(string line, string key, out string value)
    {
        value = string.Empty;
        int colon = line.IndexOf(':');
        if (colon <= 0 || !line[..colon].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        value = line[(colon + 1)..];
        return true;
    }

    /// <summary>Splits into at most <paramref name="count"/> fields; the last keeps any commas.</summary>
    private static string[] SplitFields(string value, int count) => value.Split(',', count);

    /// <summary>Style Bold: -1/1 bold, 0 regular; libass also accepts a weight.</summary>
    private static int ParseStyleBold(string value)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number))
        {
            return 400;
        }

        return number switch
        {
            0 => 400,
            -1 or 1 => 700,
            >= 100 => number,
            _ => 700,
        };
    }

    /// <summary>\b0 regular, \b1 bold, \b100…\b900 an explicit weight.</summary>
    private static int ParseOverrideBold(string value, int styleWeight)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number))
        {
            return styleWeight;
        }

        return number switch
        {
            0 => 400,
            1 => 700,
            >= 100 => number,
            _ => styleWeight,
        };
    }

    private static bool ParseFlag(string value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number) && number != 0;
}
