using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using VideoQuality.Core.Localization;

namespace VideoQuality.Core.Fonts;

/// <summary>Inputs for one subsetting run.</summary>
public sealed class FontSubsetOptions
{
    /// <summary>Scripts that will be shipped together (for example the JPSC and JPTC tracks).</summary>
    public required IReadOnlyList<string> Scripts { get; init; }

    /// <summary>Folders searched (recursively) for the fonts the scripts use.</summary>
    public required IReadOnlyList<string> FontDirectories { get; init; }

    /// <summary>Renamed scripts go here, subset fonts into its <c>fonts</c> subfolder.</summary>
    public required string OutputDirectory { get; init; }
}

/// <summary>One subset font that was written.</summary>
public sealed record SubsetFont(
    string NewFamily,
    string SourcePath,
    int SourceFace,
    string OutputPath,
    IReadOnlyList<string> ScriptNames,
    int Characters,
    long SourceBytes,
    long SubsetBytes);

/// <summary>What happened, for the UI and the CLI to report.</summary>
public sealed class FontSubsetReport
{
    public List<SubsetFont> Fonts { get; } = [];

    public List<string> ScriptsWritten { get; } = [];

    /// <summary>Font names used by the scripts that no file in the font folders provides.</summary>
    public List<FontRequest> MissingFonts { get; } = [];

    /// <summary>Requests rendered with synthetic bold/italic because no matching face exists.</summary>
    public List<(FontRequest Request, FontMatch Match)> Synthesized { get; } = [];

    /// <summary>Characters the original font has no glyph for either (the renderer will fall back).</summary>
    public List<(string Font, IReadOnlyList<int> Codepoints)> MissingGlyphs { get; } = [];

    public List<(string Path, string Error)> UnreadableFonts { get; } = [];

    /// <summary>
    /// Fonts named by a style that no line actually draws with (every line using the style switches
    /// font first, or no line uses the style). Renderers never load them, so they are not shipped.
    /// </summary>
    public List<string> UnusedStyleFonts { get; } = [];

    public bool Succeeded => MissingFonts.Count == 0;
}

/// <summary>
/// Subsets the fonts used by ASS scripts and renames them, our own implementation of the classic
/// fansub step: collect every character each font draws, subset the matching face with HarfBuzz,
/// give it a new family name, and point the scripts at the new name.
/// </summary>
public static class FontSubsetter
{
    public static FontSubsetReport Run(FontSubsetOptions options, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var report = new FontSubsetReport();
        string output = Path.GetFullPath(options.OutputDirectory);
        string fontOutput = Path.Combine(output, "fonts");

        foreach (string script in options.Scripts)
        {
            if (string.Equals(Path.GetDirectoryName(Path.GetFullPath(script)), output, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(Loc.T("subset.error.sameFolder"));
            }
        }

        progress?.Report(Loc.T("subset.progress.reading"));
        var scripts = options.Scripts.Select(path => (Path: path, Script: AssScript.Load(path))).ToList();
        var requests = new Dictionary<FontRequest, HashSet<int>>();
        foreach ((_, AssScript script) in scripts)
        {
            foreach ((FontRequest request, HashSet<int> chars) in script.CollectGlyphs())
            {
                if (!requests.TryGetValue(request, out HashSet<int>? set))
                {
                    requests[request] = set = [];
                }

                set.UnionWith(chars.Where(c => c >= 0x20 && c != 0x7F));
            }
        }

        var drawn = requests.Keys.Select(r => r.Family).ToHashSet(StringComparer.OrdinalIgnoreCase);
        report.UnusedStyleFonts.AddRange(scripts
            .SelectMany(s => s.Script.StyleFontNames())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(name => !drawn.Contains(name))
            .Order(StringComparer.OrdinalIgnoreCase));

        progress?.Report(Loc.T("subset.progress.scanning"));
        FontCatalog catalog = FontCatalog.Scan(options.FontDirectories);
        report.UnreadableFonts.AddRange(catalog.Unreadable);

        // Group what each output font must contain. Family matches share one new family name per
        // original family (so \b1 still finds the bold face); a full/PostScript-name match is an
        // exact face and gets a family of its own.
        var groups = new Dictionary<string, Dictionary<FontFace, HashSet<int>>>(StringComparer.OrdinalIgnoreCase);
        var groupOfName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var namesInGroup = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach ((FontRequest request, HashSet<int> chars) in requests.OrderBy(r => r.Key.Family, StringComparer.OrdinalIgnoreCase))
        {
            FontMatch? match = catalog.Match(request);
            if (match is null)
            {
                report.MissingFonts.Add(request);
                continue;
            }

            if (match.FauxBold || match.FauxItalic)
            {
                report.Synthesized.Add((request, match));
            }

            bool byFamily = match.Face.Families.Contains(request.Family, StringComparer.OrdinalIgnoreCase);
            string groupKey = byFamily ? "family:" + match.Face.PrimaryFamily : "face:" + request.Family;
            groupOfName[request.Family] = groupKey;

            if (!groups.TryGetValue(groupKey, out Dictionary<FontFace, HashSet<int>>? faces))
            {
                groups[groupKey] = faces = [];
                namesInGroup[groupKey] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            namesInGroup[groupKey].Add(request.Family);
            if (!faces.TryGetValue(match.Face, out HashSet<int>? set))
            {
                faces[match.Face] = set = [];
            }

            set.UnionWith(chars);
        }

        Directory.CreateDirectory(fontOutput);
        var newNameOfGroup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var fileCache = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        byte[] Bytes(string path) => fileCache.TryGetValue(path, out byte[]? b) ? b : fileCache[path] = File.ReadAllBytes(path);

        foreach ((string groupKey, Dictionary<FontFace, HashSet<int>> faces) in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string newFamily = NewFamilyName(groupKey, faces.Keys.Select(f => Bytes(f.Path)));
            newNameOfGroup[groupKey] = newFamily;
            bool several = faces.Count > 1;

            foreach ((FontFace face, HashSet<int> chars) in faces)
            {
                progress?.Report(Loc.T("subset.progress.subsetting", face, chars.Count));
                byte[] source = Bytes(face.Path);
                byte[] subset = HarfBuzzSubset.Subset(source, face.Index, chars);

                // Rename: family, full, PostScript and unique-ID names; style names and the
                // copyright/license records stay as they are.
                SfntFont font = SfntFont.Load(subset);
                string style = face.Subfamily;
                bool regular = style.Equals("Regular", StringComparison.OrdinalIgnoreCase);
                string full = regular ? newFamily : $"{newFamily} {style}";
                string postScript = (regular ? newFamily : $"{newFamily}-{style}").Replace(" ", string.Empty, StringComparison.Ordinal);
                font.ReplaceNames(new Dictionary<ushort, string>
                {
                    [SfntFont.NameFamily] = newFamily,
                    [3] = $"{postScript};VideoQuality subset",
                    [SfntFont.NameFull] = full,
                    [SfntFont.NamePostScript] = postScript,
                    [SfntFont.NameTypographicFamily] = newFamily,
                });
                byte[] renamed = font.ToBytes();

                // Verify: everything the original could draw, the subset can draw.
                HashSet<int> missingInOriginal = HarfBuzzSubset.MissingGlyphs(source, face.Index, chars);
                HashSet<int> missingInSubset = HarfBuzzSubset.MissingGlyphs(renamed, 0, chars);
                missingInSubset.ExceptWith(missingInOriginal);
                if (missingInSubset.Count > 0)
                {
                    throw new InvalidOperationException(
                        Loc.T("subset.error.lostGlyphs", face, missingInSubset.Count, missingInSubset.First().ToString("X4", CultureInfo.InvariantCulture)));
                }

                if (missingInOriginal.Count > 0)
                {
                    report.MissingGlyphs.Add((face.ToString(), missingInOriginal.Order().ToList()));
                }

                string fileName = (several ? $"{newFamily}-{style}" : newFamily).Replace(' ', '_') + (face.IsCff ? ".otf" : ".ttf");
                string outputPath = Path.Combine(fontOutput, fileName);
                File.WriteAllBytes(outputPath, renamed);

                report.Fonts.Add(new SubsetFont(
                    newFamily,
                    face.Path,
                    face.Index,
                    outputPath,
                    namesInGroup[groupKey].Order(StringComparer.OrdinalIgnoreCase).ToList(),
                    chars.Count,
                    source.Length,
                    renamed.Length));
            }
        }

        var renames = groupOfName.ToDictionary(p => p.Key, p => newNameOfGroup[p.Value], StringComparer.OrdinalIgnoreCase);
        var comments = renames
            .OrderBy(p => p.Value, StringComparer.Ordinal)
            .Select(p => $"Font Subset: {p.Value} - {p.Key}")
            .ToList();

        foreach ((string path, AssScript script) in scripts)
        {
            script.RenameFonts(renames, comments);
            string target = Path.Combine(output, Path.GetFileName(path));
            script.Save(target);
            report.ScriptsWritten.Add(target);
        }

        progress?.Report(Loc.T("subset.progress.done", report.Fonts.Count, report.ScriptsWritten.Count));
        return report;
    }

    /// <summary>
    /// Eight uppercase letters and digits from the original family and the font files' contents:
    /// stable across runs (re-subsetting an episode keeps the names), different for different fonts.
    /// </summary>
    internal static string NewFamilyName(string groupKey, IEnumerable<byte[]> files)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(groupKey.ToUpperInvariant()));
        foreach (byte[] file in files.OrderBy(f => f.Length))
        {
            hash.AppendData(SHA256.HashData(file));
        }

        const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        byte[] digest = hash.GetHashAndReset();
        var name = new StringBuilder(8);
        for (int i = 0; i < 8; i++)
        {
            name.Append(Alphabet[digest[i] % Alphabet.Length]);
        }

        // Start with a letter so no renderer mistakes it for a number.
        if (char.IsDigit(name[0]))
        {
            name[0] = Alphabet[digest[8] % 24];
        }

        return name.ToString();
    }
}
