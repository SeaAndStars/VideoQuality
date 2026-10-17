using System.Globalization;
using System.Text.RegularExpressions;
using VideoQuality.Core.Localization;

namespace VideoQuality.Tests;

// The tests read catalogs directly instead of switching Loc's language: the language is
// process-wide and other tests run in parallel against the English text.
public partial class LocalizationTests
{
    public static TheoryData<string> Languages => [.. Loc.Languages.Select(l => l.Code)];

    [Theory]
    [MemberData(nameof(Languages))]
    public void Every_catalog_has_exactly_the_english_keys(string code)
    {
        Dictionary<string, string> catalog = Loc.LoadCatalog(code);

        Assert.Empty(Loc.Keys.Except(catalog.Keys));
        Assert.Empty(catalog.Keys.Except(Loc.Keys));
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void Translations_keep_the_placeholders_and_format(string code)
    {
        Dictionary<string, string> english = Loc.LoadCatalog(Loc.English);
        foreach ((string key, string text) in Loc.LoadCatalog(code))
        {
            Assert.True(Placeholders(english[key]).SequenceEqual(Placeholders(text)), $"{code} {key}: placeholders differ");

            // Every placeholder is fed something formattable; a broken format string throws.
            object?[] args = Enumerable.Repeat<object?>(1.5, 10).ToArray();
            _ = string.Format(CultureInfo.InvariantCulture, text, args);
        }
    }

    [Fact]
    public void Every_key_the_code_uses_is_defined()
    {
        string root = RepositoryRoot();
        var used = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string file in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
                     .Where(f => f.EndsWith(".axaml", StringComparison.Ordinal) || f.EndsWith(".cs", StringComparison.Ordinal))
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                                 !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                                 !f.EndsWith("LocalizationTests.cs", StringComparison.Ordinal)))
        {
            string text = File.ReadAllText(file);
            foreach (Match match in XamlKey().Matches(text).Concat(CodeKey().Matches(text)))
            {
                used.Add(match.Groups[1].Value);
            }
        }

        Assert.NotEmpty(used);
        // Keys built at run time end with '.'; their completions are listed explicitly.
        var dynamicKeys = new Dictionary<string, string[]>
        {
            ["track.type."] = Enum.GetNames<Core.Mkv.MkvTrackType>(),
            ["mediainfo.kind."] = Enum.GetNames<Core.Media.StreamKind>(),
        };
        IEnumerable<string> expanded = used.SelectMany(key => key.EndsWith('.')
            ? dynamicKeys[key].Select(name => key + name.ToLowerInvariant())
            : [key]);
        string[] undefined = [.. expanded.Where(key => !Loc.Keys.Contains(key))];
        Assert.True(undefined.Length == 0, "undefined keys: " + string.Join(", ", undefined));
    }

    [Theory]
    [InlineData("zh-CN", "zh-Hans")]
    [InlineData("zh_TW.UTF-8", "zh-Hans")]
    [InlineData("zh-Hans", "zh-Hans")]
    [InlineData("en-US", "en")]
    [InlineData("fr-FR", "en")]
    [InlineData(null, "en")]
    public void Language_tags_resolve_to_a_catalog(string? tag, string expected) =>
        Assert.Equal(expected, Loc.Resolve(tag));

    [Fact]
    public void Chinese_catalog_is_translated()
    {
        Assert.Equal("混流", Loc.LoadCatalog("zh-Hans")["nav.mux"]);
        Assert.Equal("Mux", Loc.LoadCatalog(Loc.English)["nav.mux"]);
    }

    [Fact]
    public void Unknown_key_falls_back_to_the_key() =>
        Assert.Equal("no.such.key", Loc.T("no.such.key"));

    private static IEnumerable<string> Placeholders(string text) =>
        PlaceholderPattern().Matches(text).Select(m => m.Value).Order(StringComparer.Ordinal);

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "VideoQuality.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException("repository root not found");
    }

    [GeneratedRegex(@"\{l:L ([\w.]+)\}")]
    private static partial Regex XamlKey();

    [GeneratedRegex(@"Loc\.T\(\s*""([\w.]+)""")]
    private static partial Regex CodeKey();

    [GeneratedRegex(@"\{\d+[^}]*\}")]
    private static partial Regex PlaceholderPattern();
}
