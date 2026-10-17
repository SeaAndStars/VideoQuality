namespace VideoQuality.Core.Mkv;

/// <summary>Track language and name from fansub file-naming conventions.</summary>
public static class SubtitleNaming
{
    private static readonly char[] Separators = [' ', '.', '_', '-', '[', ']', '(', ')', '&', '+'];

    /// <summary>
    /// JPSC/CHS/SC/GB/简 → zh-Hans "简体中文"; JPTC/CHT/TC/BIG5/繁 → zh-Hant "繁體中文"; JP/JA →
    /// Japanese; EN → English. Null when the name says nothing.
    /// </summary>
    public static (string Language, string Name)? Guess(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        string[] tokens = name.Split(Separators, StringSplitOptions.RemoveEmptyEntries).Select(t => t.ToUpperInvariant()).ToArray();

        if (tokens.Any(t => t is "JPSC" or "CHS" or "SC" or "GB" or "ZH-HANS" or "SCJP") || name.Contains('简'))
        {
            return ("zh-Hans", "简体中文");
        }

        if (tokens.Any(t => t is "JPTC" or "CHT" or "TC" or "BIG5" or "ZH-HANT" or "TCJP") || name.Contains('繁'))
        {
            return ("zh-Hant", "繁體中文");
        }

        if (tokens.Any(t => t is "JP" or "JA" or "JPN" or "JAPANESE"))
        {
            return ("ja", "日本語");
        }

        if (tokens.Any(t => t is "EN" or "ENG" or "ENGLISH"))
        {
            return ("en", "English");
        }

        return null;
    }
}
