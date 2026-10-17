using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace VideoQuality.Core.Localization;

/// <summary>A user-interface language the app ships a catalog for.</summary>
public sealed record UiLanguage(string Code, string NativeName);

/// <summary>
/// User-facing text, looked up by key in the embedded catalogs (<c>Localization/{code}.json</c>).
/// The language is chosen once at startup; logs and the CLI stay in English. A key missing from a
/// catalog falls back to English, then to the key itself, so a gap shows up instead of crashing.
/// </summary>
public static class Loc
{
    public const string English = "en";

    public static IReadOnlyList<UiLanguage> Languages { get; } =
    [
        new(English, "English"),
        new("zh-Hans", "简体中文"),
    ];

    private static readonly Dictionary<string, string> Fallback = LoadCatalog(English);
    private static Dictionary<string, string> _strings = Fallback;

    /// <summary>The active catalog's code, e.g. <c>en</c> or <c>zh-Hans</c>.</summary>
    public static string Language { get; private set; } = English;

    /// <summary>
    /// Switches catalogs. <c>null</c> or empty follows the system: the UI culture, or on Linux
    /// (invariant globalization) LC_ALL / LC_MESSAGES / LANG.
    /// </summary>
    public static void SetLanguage(string? code)
    {
        string resolved = Resolve(string.IsNullOrWhiteSpace(code) ? SystemLanguageTag() : code);
        Language = resolved;
        _strings = resolved == English ? Fallback : LoadCatalog(resolved);
    }

    /// <summary>The catalog code a language tag maps to (<c>zh-CN</c>, <c>zh_TW.UTF-8</c> → <c>zh-Hans</c>).</summary>
    public static string Resolve(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return English;
        }

        string normalized = tag.Split('.', '@')[0].Replace('_', '-');
        foreach (UiLanguage language in Languages)
        {
            if (normalized.Equals(language.Code, StringComparison.OrdinalIgnoreCase))
            {
                return language.Code;
            }
        }

        // Only one Chinese catalog so far: every Chinese variant gets it.
        return normalized.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh-Hans" : English;
    }

    /// <summary>The catalog "follow the system" picks on this machine.</summary>
    public static string SystemLanguage => Resolve(SystemLanguageTag());

    /// <summary>Whether the active catalog (or English) has text for the key.</summary>
    public static bool Has(string key) => _strings.ContainsKey(key) || Fallback.ContainsKey(key);

    public static string T(string key) =>
        _strings.TryGetValue(key, out string? value) || Fallback.TryGetValue(key, out value) ? value : key;

    /// <summary>Formats with the invariant culture, like the rest of the app's numbers.</summary>
    public static string T(string key, params object?[] args) =>
        string.Format(CultureInfo.InvariantCulture, T(key), args);

    /// <summary>Every key in the English catalog (for tests).</summary>
    public static IReadOnlyCollection<string> Keys => Fallback.Keys;

    internal static Dictionary<string, string> LoadCatalog(string code)
    {
        using Stream? stream = typeof(Loc).Assembly.GetManifestResourceStream($"VideoQuality.Core.Localization.{code}.json");
        if (stream is null)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        return JsonSerializer.Deserialize(stream, LocJsonContext.Default.DictionaryStringString)
               ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }

    private static string? SystemLanguageTag()
    {
        string name = CultureInfo.CurrentUICulture.Name;
        if (name.Length > 0)
        {
            return name;
        }

        foreach (string variable in new[] { "LC_ALL", "LC_MESSAGES", "LANG" })
        {
            if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value && value is not "C" and not "POSIX")
            {
                return value;
            }
        }

        return null;
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class LocJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
