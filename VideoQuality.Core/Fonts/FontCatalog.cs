namespace VideoQuality.Core.Fonts;

/// <summary>One face of a font file (a TTC/OTC holds several).</summary>
public sealed class FontFace
{
    internal FontFace(string path, int index, SfntFont font)
    {
        Path = path;
        Index = index;
        Families = font.GetNames(SfntFont.NameFamily).Concat(font.GetNames(SfntFont.NameTypographicFamily))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        FullNames = font.GetNames(SfntFont.NameFull).ToList();
        PostScriptName = font.GetName(SfntFont.NamePostScript) ?? string.Empty;
        Subfamily = font.GetName(SfntFont.NameSubfamily) ?? "Regular";
        Weight = font.Weight;
        Italic = font.Italic;
        IsCff = font.IsCff;
        IsVariable = font.IsVariable;
    }

    public string Path { get; }

    public int Index { get; }

    /// <summary>Family names (IDs 1 and 16) in every language, English first.</summary>
    public IReadOnlyList<string> Families { get; }

    public IReadOnlyList<string> FullNames { get; }

    public string PostScriptName { get; }

    public string Subfamily { get; }

    public int Weight { get; }

    public bool Italic { get; }

    public bool IsCff { get; }

    public bool IsVariable { get; }

    public string PrimaryFamily => Families.FirstOrDefault() ?? PostScriptName;

    public override string ToString() => $"{PrimaryFamily} {Subfamily} ({System.IO.Path.GetFileName(Path)}#{Index})";
}

/// <summary>How a requested font was resolved.</summary>
/// <param name="Face">The face that will render it.</param>
/// <param name="FauxBold">Bold requested but the face is not bold: the renderer will embolden.</param>
/// <param name="FauxItalic">Italic requested but the face is upright: the renderer will slant it.</param>
public sealed record FontMatch(FontFace Face, bool FauxBold, bool FauxItalic);

/// <summary>
/// The fonts available for a set of scripts, matched the way ASS renderers do: a script's font
/// name may be any family, full or PostScript name in any language; full/PostScript names pick
/// that exact face, family names pick the closest weight and slant.
/// </summary>
public sealed class FontCatalog
{
    private static readonly string[] Extensions = [".ttf", ".otf", ".ttc", ".otc"];

    private FontCatalog(List<FontFace> faces, List<(string Path, string Error)> unreadable)
    {
        Faces = faces;
        Unreadable = unreadable;
    }

    public IReadOnlyList<FontFace> Faces { get; }

    /// <summary>Files that look like fonts but could not be parsed.</summary>
    public IReadOnlyList<(string Path, string Error)> Unreadable { get; }

    public static FontCatalog Scan(IEnumerable<string> directories)
    {
        var faces = new List<FontFace>();
        var unreadable = new List<(string, string)>();

        foreach (string directory in directories.Where(Directory.Exists))
        {
            foreach (string path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                         .Where(p => Extensions.Contains(System.IO.Path.GetExtension(p), StringComparer.OrdinalIgnoreCase))
                         .Order(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    byte[] bytes = File.ReadAllBytes(path);
                    int count = SfntFont.CountFaces(bytes);
                    for (int index = 0; index < count; index++)
                    {
                        faces.Add(new FontFace(path, index, SfntFont.Load(bytes, index)));
                    }
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException)
                {
                    unreadable.Add((path, ex.Message));
                }
            }
        }

        return new FontCatalog(faces, unreadable);
    }

    public FontMatch? Match(FontRequest request)
    {
        string name = request.Family.Trim();
        FontFace chosen;

        // Family names win: a Regular face's full name usually equals its family name, so checking
        // full names first would lock "Family" + \b1 to the Regular face.
        var family = Faces.Where(f => f.Families.Contains(name, StringComparer.OrdinalIgnoreCase)).ToList();
        if (family.Count > 0)
        {
            // Slant mismatch outweighs any weight difference, as in GDI's font mapper.
            chosen = family
                .OrderBy(f => (f.Italic != request.Italic ? 10_000 : 0) + Math.Abs(f.Weight - request.Weight))
                .ThenBy(f => f.Path, StringComparer.Ordinal)
                .First();
        }
        else
        {
            // Otherwise a full or PostScript name identifies one face exactly.
            FontFace? exact = Faces.FirstOrDefault(f =>
                f.FullNames.Contains(name, StringComparer.OrdinalIgnoreCase) ||
                string.Equals(f.PostScriptName, name, StringComparison.OrdinalIgnoreCase));
            if (exact is null)
            {
                return null;
            }

            chosen = exact;
        }

        return new FontMatch(
            chosen,
            FauxBold: request.Weight >= 600 && chosen.Weight < 550,
            FauxItalic: request.Italic && !chosen.Italic);
    }
}
