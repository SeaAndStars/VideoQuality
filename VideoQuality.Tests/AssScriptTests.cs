using System.Text;
using VideoQuality.Core.Fonts;

namespace VideoQuality.Tests;

public class AssScriptTests
{
    private const string Header = """
        [Script Info]
        ScriptType: v4.00+

        [V4+ Styles]
        Format: Name, Fontname, Fontsize, PrimaryColour, Bold, Italic
        Style: Default,Main Font,50,&H00FFFFFF,0,0
        Style: Sign,Sign Font,40,&H00FFFFFF,-1,0
        Style: Vertical,@Tall Font,40,&H00FFFFFF,0,0

        [Events]
        Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text

        """;

    private static Dictionary<FontRequest, HashSet<int>> Collect(params string[] dialogue) =>
        AssScript.Parse(Encoding.UTF8.GetBytes(Header + string.Join("\n", dialogue))).CollectGlyphs();

    private static string Chars(HashSet<int> set) => string.Concat(set.Order().Select(char.ConvertFromUtf32));

    [Fact]
    public void Style_font_weight_and_slant_come_from_the_style()
    {
        var fonts = Collect("Dialogue: 0,0:00:00.00,0:00:01.00,Sign,,0,0,0,,ab");

        Assert.Equal("ab", Chars(fonts[new FontRequest("Sign Font", 700, false)]));
    }

    [Fact]
    public void Text_may_contain_commas()
    {
        var fonts = Collect("Dialogue: 0,0:00:00.00,0:00:01.00,Default,,0,0,0,,a,b");

        Assert.Equal(",ab", Chars(fonts[new FontRequest("Main Font", 400, false)]));
    }

    [Fact]
    public void Fn_before_any_text_means_the_style_font_draws_nothing()
    {
        var fonts = Collect(@"Dialogue: 0,0:00:00.00,0:00:01.00,Default,,0,0,0,,{\pos(1,2)\fnOther}xy");

        Assert.False(fonts.ContainsKey(new FontRequest("Main Font", 400, false)));
        Assert.Equal("xy", Chars(fonts[new FontRequest("Other", 400, false)]));
    }

    [Fact]
    public void Bold_italic_and_reset_tags_change_the_request()
    {
        var fonts = Collect(@"Dialogue: 0,0:00:00.00,0:00:01.00,Default,,0,0,0,,a{\b1}b{\i1}c{\r}d{\b700\rSign}e");

        Assert.Equal("ad", Chars(fonts[new FontRequest("Main Font", 400, false)]));
        Assert.Equal("b", Chars(fonts[new FontRequest("Main Font", 700, false)]));
        Assert.Equal("c", Chars(fonts[new FontRequest("Main Font", 700, true)]));
        Assert.Equal("e", Chars(fonts[new FontRequest("Sign Font", 700, false)]));
    }

    [Fact]
    public void Drawings_line_breaks_and_comments_draw_no_glyphs()
    {
        var fonts = Collect(@"Dialogue: 0,0:00:00.00,0:00:01.00,Default,,0,0,0,,{\p1}m 0 0 l 10 10{\p0}a\Nb\nc\hd",
                            "Comment: 0,0:00:00.00,0:00:01.00,Default,,0,0,0,,zzz");

        Assert.Equal("abcd ", Chars(fonts[new FontRequest("Main Font", 400, false)]));
    }

    [Fact]
    public void Vertical_fonts_match_their_horizontal_family()
    {
        var fonts = Collect("Dialogue: 0,0:00:00.00,0:00:01.00,Vertical,,0,0,0,,縦😀");

        Assert.Equal("縦😀", Chars(fonts[new FontRequest("Tall Font", 400, false)]));
    }

    [Fact]
    public void Rename_touches_styles_and_fn_tags_and_keeps_the_vertical_prefix()
    {
        var script = AssScript.Parse(Encoding.UTF8.GetBytes(Header +
            @"Dialogue: 0,0:00:00.00,0:00:01.00,Default,,0,0,0,,{\fnSign Font\b1}a{\fn@Tall Font}b"));

        script.RenameFonts(
            new Dictionary<string, string> { ["main font"] = "AAAA", ["Sign Font"] = "BBBB", ["Tall Font"] = "CCCC" },
            ["Font Subset: AAAA - Main Font"]);
        string text = Encoding.UTF8.GetString(script.ToBytes());

        Assert.Contains("Style: Default,AAAA,50,", text);
        Assert.Contains("Style: Vertical,@CCCC,40,", text);
        Assert.Contains(@"{\fnBBBB\b1}a{\fn@CCCC}b", text);
        Assert.Contains("[Script Info]\n; Font Subset: AAAA - Main Font", text);
    }

    [Theory]
    [InlineData("utf-8-bom")]
    [InlineData("utf-16le-bom")]
    [InlineData("gb18030")]
    public void Encoding_and_byte_order_mark_round_trip(string kind)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        string text = Header.Replace("\n", "\r\n", StringComparison.Ordinal) + "Dialogue: 0,0:00:00.00,0:00:01.00,Default,,0,0,0,,中文";
        byte[] bytes = kind switch
        {
            "utf-8-bom" => [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(text)],
            "utf-16le-bom" => [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(text)],
            _ => Encoding.GetEncoding("GB18030").GetBytes(text),
        };

        AssScript script = AssScript.Parse(bytes);

        Assert.Equal(bytes, script.ToBytes());
        Assert.Equal("中文", Chars(script.CollectGlyphs()[new FontRequest("Main Font", 400, false)]).Replace(",", ""));
    }
}
