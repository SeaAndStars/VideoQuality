using System.Buffers.Binary;
using System.Text;
using VideoQuality.Core.Fonts;

namespace VideoQuality.Tests;

/// <summary>Uses the DejaVu fonts in Fixtures/fonts.</summary>
public sealed class FontSubsetTests : IDisposable
{
    private static readonly string DejaVu = Path.Combine(AppContext.BaseDirectory, "Fixtures", "fonts", "DejaVuSans.ttf");
    private static readonly string DejaVuBold = Path.Combine(AppContext.BaseDirectory, "Fixtures", "fonts", "DejaVuSans-Bold.ttf");
    private readonly string _root = Directory.CreateTempSubdirectory("vq-fonts-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static void RequireFonts() => Assert.True(File.Exists(DejaVu) && File.Exists(DejaVuBold), "Fixtures/fonts is missing");

    [Fact]
    public void Name_table_rewrite_keeps_the_file_valid()
    {
        RequireFonts();
        SfntFont font = SfntFont.Load(File.ReadAllBytes(DejaVu));
        string? copyright = font.GetName(SfntFont.NameCopyright);

        font.ReplaceNames(new Dictionary<ushort, string> { [SfntFont.NameFamily] = "TESTNAME", [SfntFont.NameFull] = "TESTNAME" });
        byte[] bytes = font.ToBytes();
        SfntFont reloaded = SfntFont.Load(bytes);

        Assert.Equal("TESTNAME", reloaded.GetName(SfntFont.NameFamily));
        Assert.Equal(copyright, reloaded.GetName(SfntFont.NameCopyright));
        // OpenType: the whole file sums to 0xB1B0AFBA once checkSumAdjustment is in place.
        Assert.Equal(0xB1B0AFBAu, SfntChecksum(bytes));
    }

    [Fact]
    public void HarfBuzz_subset_keeps_exactly_the_requested_characters()
    {
        RequireFonts();
        byte[] source = File.ReadAllBytes(DejaVu);
        int[] wanted = "Hello, 世界".EnumerateRunes().Select(r => r.Value).Distinct().ToArray();

        byte[] subset = HarfBuzzSubset.Subset(source, 0, wanted);

        Assert.True(subset.Length < source.Length / 10);
        // DejaVu has no CJK: those two are missing in both, nothing else is lost.
        Assert.Equal(HarfBuzzSubset.MissingGlyphs(source, 0, wanted), HarfBuzzSubset.MissingGlyphs(subset, 0, wanted));
        Assert.Empty(HarfBuzzSubset.MissingGlyphs(subset, 0, "Helo,".Select(c => (int)c)));
    }

    [Fact]
    public void Matching_prefers_a_real_bold_face_and_reports_synthetic_italic()
    {
        RequireFonts();
        File.Copy(DejaVu, Path.Combine(_root, "a.ttf"));
        File.Copy(DejaVuBold, Path.Combine(_root, "b.ttf"));
        FontCatalog catalog = FontCatalog.Scan([_root]);

        FontMatch bold = catalog.Match(new FontRequest("dejavu sans", 700, false))!;
        FontMatch italic = catalog.Match(new FontRequest("DejaVu Sans", 400, true))!;
        FontMatch byFullName = catalog.Match(new FontRequest("DejaVu Sans Bold", 400, false))!;

        Assert.True(bold.Face.Weight >= 700);
        Assert.False(bold.FauxBold);
        Assert.True(italic.FauxItalic);
        Assert.True(byFullName.Face.Weight >= 700);
        Assert.Null(catalog.Match(new FontRequest("No Such Font", 400, false)));
    }

    [Fact]
    public void End_to_end_subset_renames_fonts_and_scripts()
    {
        RequireFonts();
        string fonts = Directory.CreateDirectory(Path.Combine(_root, "fonts")).FullName;
        File.Copy(DejaVu, Path.Combine(fonts, "DejaVuSans.ttf"));
        File.Copy(DejaVuBold, Path.Combine(fonts, "DejaVuSans-Bold.ttf"));
        string script = Path.Combine(_root, "ep01.ass");
        File.WriteAllText(script, """
            [Script Info]
            ScriptType: v4.00+

            [V4+ Styles]
            Format: Name, Fontname, Fontsize, Bold, Italic
            Style: Default,DejaVu Sans,50,0,0
            Style: Unused,Missing Font,50,0,0

            [Events]
            Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
            Dialogue: 0,0:00:00.00,0:00:01.00,Default,,0,0,0,,Hi {\b1}there
            """, new UTF8Encoding(true));

        FontSubsetReport report = FontSubsetter.Run(new FontSubsetOptions
        {
            Scripts = [script],
            FontDirectories = [fonts],
            OutputDirectory = Path.Combine(_root, "out"),
        });

        Assert.True(report.Succeeded);
        Assert.Equal(2, report.Fonts.Count);
        Assert.Single(report.Fonts.Select(f => f.NewFamily).Distinct()); // one family for both weights
        Assert.Equal(["Missing Font"], report.UnusedStyleFonts);

        string newFamily = report.Fonts[0].NewFamily;
        string written = File.ReadAllText(report.ScriptsWritten.Single());
        Assert.Contains($"Style: Default,{newFamily},50", written);
        Assert.Contains($"; Font Subset: {newFamily} - DejaVu Sans", written);
        foreach (SubsetFont font in report.Fonts)
        {
            Assert.Equal(newFamily, SfntFont.Load(File.ReadAllBytes(font.OutputPath)).GetName(SfntFont.NameFamily));
        }

        // Stable: running again gives the same names.
        FontSubsetReport again = FontSubsetter.Run(new FontSubsetOptions
        {
            Scripts = [script],
            FontDirectories = [fonts],
            OutputDirectory = Path.Combine(_root, "out2"),
        });
        Assert.Equal(newFamily, again.Fonts[0].NewFamily);
    }

    private static uint SfntChecksum(byte[] data)
    {
        uint sum = 0;
        for (int i = 0; i + 4 <= data.Length; i += 4)
        {
            sum = unchecked(sum + BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(i)));
        }

        return sum;
    }
}

/// <summary>Reporting paths of the subsetter: missing, synthetic, CFF, exact-face and broken fonts.</summary>
public sealed class FontSubsetReportTests : IDisposable
{
    private static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures", "fonts");
    private readonly string _root = Directory.CreateTempSubdirectory("vq-subset-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Fonts()
    {
        string fonts = Directory.CreateDirectory(Path.Combine(_root, "fonts")).FullName;
        File.Copy(Path.Combine(Fixtures, "DejaVuSans.ttf"), Path.Combine(fonts, "DejaVuSans.ttf"));
        File.Copy(Path.Combine(Fixtures, "VQTestCff.otf"), Path.Combine(fonts, "VQTestCff.otf"));
        File.WriteAllText(Path.Combine(fonts, "broken.ttf"), "not a font");
        File.WriteAllText(Path.Combine(fonts, "notes.txt"), "ignored");
        return fonts;
    }

    private string Script(string dialogue)
    {
        string script = Path.Combine(_root, "ep.ass");
        File.WriteAllText(script, $"""
            [V4+ Styles]
            Format: Name, Fontname, Bold, Italic
            Style: Default,DejaVu Sans,0,0

            [Events]
            Format: Style, Text
            {dialogue}
            """);
        return script;
    }

    [Fact]
    public void Report_lists_missing_synthetic_unreadable_and_uncovered_characters()
    {
        string script = Script("""
            Dialogue: Default,Hi 世{\fnVQ Test CFF}Box{\b1}B{\b0\fnDejaVuSans}ps{\fn\i1}it{\fnNope}x
            """);
        var progress = new List<string>();
        FontSubsetReport report = FontSubsetter.Run(
            new FontSubsetOptions { Scripts = [script], FontDirectories = [Fonts(), Path.Combine(_root, "absent")], OutputDirectory = Path.Combine(_root, "out") },
            new SyncProgress<string>(progress.Add));

        Assert.False(report.Succeeded);
        Assert.Equal("Nope", Assert.Single(report.MissingFonts).Family);
        Assert.Equal(2, report.Synthesized.Count);
        Assert.Contains(report.Synthesized, s => s.Match.FauxBold && !s.Match.FauxItalic);
        Assert.Contains(report.Synthesized, s => s.Match.FauxItalic && !s.Match.FauxBold);
        Assert.Equal("broken.ttf", Path.GetFileName(Assert.Single(report.UnreadableFonts).Path));
        Assert.Equal([0x4E16], Assert.Single(report.MissingGlyphs).Codepoints);
        Assert.Contains(report.Fonts, f => f.OutputPath.EndsWith(".otf", StringComparison.Ordinal));
        Assert.Equal(3, report.Fonts.Select(f => f.NewFamily).Distinct().Count()); // DejaVu family, its PostScript face, the CFF font
        Assert.NotEmpty(progress);
    }

    [Fact]
    public void A_subset_that_loses_glyphs_is_an_error()
    {
        string script = Script("Dialogue: Default,abc");
        var options = new FontSubsetOptions
        {
            Scripts = [script],
            FontDirectories = [Fonts()],
            OutputDirectory = Path.Combine(_root, "out"),
            Subsetter = (font, face, chars) => HarfBuzzSubset.Subset(font, face, chars.Where(c => c != 'b')),
        };
        Assert.Contains("0062", Assert.Throws<InvalidOperationException>(() => FontSubsetter.Run(options)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Output_must_not_be_the_scripts_folder()
    {
        string script = Script("Dialogue: Default,a");
        Assert.Throws<InvalidOperationException>(() => FontSubsetter.Run(new FontSubsetOptions { Scripts = [script], FontDirectories = [], OutputDirectory = _root }));
    }

    [Fact]
    public void Garbage_subsets_to_an_empty_font()
    {
        // HarfBuzz turns unreadable input into an empty font rather than failing; the null checks
        // guard allocation failures.
        Assert.Equal(12, HarfBuzzSubset.Subset("not a font"u8.ToArray(), 0, [65]).Length);
        Assert.Throws<InvalidOperationException>(() => HarfBuzzSubset.Require(IntPtr.Zero, "failed"));
        Assert.Equal(1, HarfBuzzSubset.Require(1, "unused"));
        Assert.False(string.IsNullOrEmpty(HarfBuzzSubset.Version));
    }

    [Fact]
    public void New_family_names_always_start_with_a_letter()
    {
        for (int i = 0; i < 200; i++)
        {
            string name = FontSubsetter.NewFamilyName("family:" + i, []);
            Assert.Equal(8, name.Length);
            Assert.True(char.IsLetter(name[0]), name);
        }
    }

    [Fact]
    public void Faces_without_names_fall_back_to_defaults()
    {
        SfntFont font = SfntFont.Load(File.ReadAllBytes(Path.Combine(Fixtures, "DejaVuSans.ttf")));
        font.Tables.Remove("name");
        string folder = Directory.CreateDirectory(Path.Combine(_root, "nameless")).FullName;
        File.WriteAllBytes(Path.Combine(folder, "x.ttf"), font.ToBytes());

        FontFace face = Assert.Single(FontCatalog.Scan([folder]).Faces);
        Assert.Equal((string.Empty, "Regular", string.Empty), (face.PostScriptName, face.Subfamily, face.PrimaryFamily));
        Assert.Equal(" Regular (x.ttf#0)", face.ToString());
    }
}
