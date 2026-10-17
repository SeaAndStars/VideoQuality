namespace VideoQuality.Core.Mkv;

/// <summary>How tracks are tagged when muxing.</summary>
public enum TrackNamingProfile
{
    /// <summary>Studio GreenTea rules: subtitles <c>zh</c> named JPSC/JPTC/SC/TC; audio <c>ja</c>, unnamed.</summary>
    GreenTea,

    /// <summary>BCP 47 script subtags and native names: zh-Hans "简体中文", zh-Hant "繁體中文".</summary>
    Generic,
}

/// <summary>Track language and name from fansub file-naming conventions.</summary>
public static class SubtitleNaming
{
    private static readonly char[] Separators = [' ', '.', '_', '-', '[', ']', '(', ')', '&', '+'];

    /// <summary>
    /// Reads JPSC/JPTC/SC/TC/CHS/CHT/GB/BIG5/简/繁/简日/繁日/JP/EN from a file name. Null when the name
    /// says nothing. The name is empty when the profile leaves that kind of track unnamed.
    /// </summary>
    public static (string Language, string Name)? Guess(string path, TrackNamingProfile profile = TrackNamingProfile.GreenTea)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        string[] tokens = name.Split(Separators, StringSplitOptions.RemoveEmptyEntries).Select(t => t.ToUpperInvariant()).ToArray();
        bool japanese = tokens.Any(t => t is "JP" or "JA" or "JPN" or "JAPANESE") || name.Contains('日');
        bool simplified = tokens.Any(t => t is "JPSC" or "SCJP" or "CHS" or "SC" or "GB" or "ZH-HANS") || name.Contains('简');
        bool traditional = tokens.Any(t => t is "JPTC" or "TCJP" or "CHT" or "TC" or "BIG5" or "ZH-HANT") || name.Contains('繁');
        bool bilingual = tokens.Any(t => t is "JPSC" or "SCJP" or "JPTC" or "TCJP") || japanese;

        if (simplified || traditional)
        {
            bool tc = traditional && !simplified;
            return profile == TrackNamingProfile.GreenTea
                ? ("zh", (bilingual ? "JP" : string.Empty) + (tc ? "TC" : "SC"))
                : tc ? ("zh-Hant", "繁體中文") : ("zh-Hans", "简体中文");
        }

        if (japanese)
        {
            return ("ja", profile == TrackNamingProfile.GreenTea ? string.Empty : "日本語");
        }

        if (tokens.Any(t => t is "EN" or "ENG" or "ENGLISH"))
        {
            return ("en", profile == TrackNamingProfile.GreenTea ? string.Empty : "English");
        }

        return null;
    }
}
