using System.Text;
using VideoQuality.Core.Fonts;

namespace VideoQuality.Tests;

/// <summary>Malformed and unusual scripts: the parser follows the renderer instead of failing.</summary>
public class AssScriptEdgeTests
{
    private static AssScript Parse(string text) => AssScript.Parse(Encoding.UTF8.GetBytes(text));

    private static string Chars(HashSet<int> set) => string.Concat(set.Order().Select(char.ConvertFromUtf32));

    [Fact]
    public void Requests_describe_themselves()
    {
        Assert.Equal("A Bold Italic", new FontRequest("A", 700, true).ToString());
        Assert.Equal("A", new FontRequest("A", 400, false).ToString());
        Assert.True(new FontRequest("A", 600, false).IsBold);
    }

    [Fact]
    public void Byte_order_marks_are_detected_and_kept()
    {
        byte[] big = [0xFE, 0xFF, .. new UnicodeEncoding(true, false).GetBytes("[Script Info]")];
        AssScript script = AssScript.Parse(big);
        Assert.True(script.HadBom);
        Assert.Equal(big, script.ToBytes());
        Assert.False(Parse("[Script Info]").HadBom);
    }

    [Fact]
    public void Scripts_without_styles_draw_in_arial_and_unknown_styles_use_the_first()
    {
        var bare = Parse("[Events]\nFormat: Style, Text\nDialogue: Nope,ab").CollectGlyphs();
        Assert.Equal("ab", Chars(bare[new FontRequest("Arial", 400, false)]));

        var fonts = Parse("""
            [V4+ Styles]
            Format: Name, Fontname, Bold, Italic
            ; a comment between styles
            Style: ,Nameless,0,0
            Style: Main,Main Font,x,x
            Style: Heavy,Heavy Font,5,0
            Style: Light,Light Font,300,1

            [Events]
            Format: Style, Text
            ; a comment between events
            Dialogue: *Main,a
            Dialogue: Missing,b
            Dialogue: Heavy,c
            Dialogue: Light,d
            """).CollectGlyphs();
        Assert.Equal("ab", Chars(fonts[new FontRequest("Main Font", 400, false)]));
        Assert.Equal("c", Chars(fonts[new FontRequest("Heavy Font", 700, false)]));
        Assert.Equal("d", Chars(fonts[new FontRequest("Light Font", 300, true)]));
    }

    [Fact]
    public void Dialogue_without_style_or_text_fields()
    {
        var fonts = Parse("""
            [V4+ Styles]
            Format: Name, Fontname
            Style: Main,Main Font

            [Events]
            Format: Text
            Dialogue: no style column
            Format: Style, Layer, Text
            Dialogue: Main
            Format: Style
            Dialogue: Main
            """).CollectGlyphs();
        FontRequest main = new("Main Font", 400, false);
        Assert.Equal(" celmnostuy", Chars(fonts[main]));
        Assert.Single(fonts);
    }

    [Fact]
    public void Override_tags_follow_the_renderer()
    {
        var fonts = Parse("""
            [V4+ Styles]
            Format: Name, Fontname, Bold, Italic
            Style: Main,Main Font,0,0
            Style: Alt,Alt Font,1,1

            [Events]
            Format: Style, Text
            Dialogue: Main,{\fnOther}a{\fn}b{\rAlt}c{\rNope}d{\b}e{\bx}f{\b50}g{\b0}h{\b500}i{\i1}j{\i}k{\px}l\hm
            Dialogue: Main,😀x\{y
            Dialogue: Main,end{\b1
            """).CollectGlyphs();
        Assert.Equal("a", Chars(fonts[new FontRequest("Other", 400, false)]));
        Assert.Equal("\\bdefghnx😀", Chars(fonts[new FontRequest("Main Font", 400, false)])); // "\{" opens an unterminated block
        Assert.Equal("c", Chars(fonts[new FontRequest("Alt Font", 700, true)]));
        Assert.Equal("iklm\u00A0", Chars(fonts[new FontRequest("Main Font", 500, false)]));
        Assert.Equal("j", Chars(fonts[new FontRequest("Main Font", 500, true)]));

    }

    [Fact]
    public void Renaming_skips_short_styles_and_unterminated_blocks_and_adds_comments_at_the_top()
    {
        AssScript script = Parse("""
            [V4+ Styles]
            Style: Early,Before Format
            Format: Name, Fontname
            ; comment
            Style: Main,Old
            Style: Short

            [Events]
            Dialogue: {\fnOld}a{\fnOld
            """);
        Assert.Empty(script.CollectGlyphs()); // a style before its format and dialogue before its format are skipped
        script.RenameFonts(new Dictionary<string, string> { ["Old"] = "New" }, ["subset"]);
        string text = Encoding.UTF8.GetString(script.ToBytes());
        Assert.StartsWith("; subset\n[V4+ Styles]", text, StringComparison.Ordinal);
        Assert.Contains("Style: Main,New", text, StringComparison.Ordinal);
        Assert.Contains("Style: Short", text, StringComparison.Ordinal);
        Assert.Contains("Style: Early,Before Format", text, StringComparison.Ordinal);
        Assert.Contains(@"Dialogue: {\fnNew}a{\fnOld", text, StringComparison.Ordinal);

        script.RenameFonts(new Dictionary<string, string>());
        Assert.Equal(text, Encoding.UTF8.GetString(script.ToBytes()));
    }
}
