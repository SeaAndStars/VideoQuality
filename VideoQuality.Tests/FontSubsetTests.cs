using System.Buffers.Binary;
using System.Text;
using VideoQuality.Core.Fonts;

namespace VideoQuality.Tests;

/// <summary>Uses DejaVu from the system font folder (present on Ubuntu and CI); skips otherwise.</summary>
public sealed class FontSubsetTests : IDisposable
{
    private const string DejaVu = "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf";
    private const string DejaVuBold = "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf";
    private readonly string _root = Directory.CreateTempSubdirectory("vq-fonts-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static void RequireFonts() => Skip.IfNot(File.Exists(DejaVu) && File.Exists(DejaVuBold), "DejaVu fonts not installed");

    [SkippableFact]
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

    [SkippableFact]
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

    [SkippableFact]
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

    [SkippableFact]
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
