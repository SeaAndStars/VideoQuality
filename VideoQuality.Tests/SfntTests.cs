using System.Buffers.Binary;
using System.Text;
using VideoQuality.Core.Fonts;

namespace VideoQuality.Tests;

/// <summary>OpenType container edge cases on fonts rebuilt from the DejaVu fixture.</summary>
public class SfntTests
{
    private static readonly byte[] DejaVu = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "fonts", "DejaVuSans.ttf"));

    private static SfntFont Font() => SfntFont.Load(DejaVu);

    private static SfntFont With(Action<Dictionary<string, byte[]>> change)
    {
        SfntFont font = Font();
        change(font.Tables);
        return SfntFont.Load(font.ToBytes());
    }

    private static byte[] NameTable(params (ushort Platform, ushort Encoding, ushort Language, ushort Id, byte[] Raw)[] records)
    {
        int header = 6 + 12 * records.Length;
        var span = new byte[header];
        BinaryPrimitives.WriteUInt16BigEndian(span.AsSpan(2), (ushort)records.Length);
        BinaryPrimitives.WriteUInt16BigEndian(span.AsSpan(4), (ushort)header);
        int offset = 0;
        var strings = new List<byte>();
        for (int i = 0; i < records.Length; i++)
        {
            int r = 6 + 12 * i;
            BinaryPrimitives.WriteUInt16BigEndian(span.AsSpan(r), records[i].Platform);
            BinaryPrimitives.WriteUInt16BigEndian(span.AsSpan(r + 2), records[i].Encoding);
            BinaryPrimitives.WriteUInt16BigEndian(span.AsSpan(r + 4), records[i].Language);
            BinaryPrimitives.WriteUInt16BigEndian(span.AsSpan(r + 6), records[i].Id);
            BinaryPrimitives.WriteUInt16BigEndian(span.AsSpan(r + 8), (ushort)records[i].Raw.Length);
            BinaryPrimitives.WriteUInt16BigEndian(span.AsSpan(r + 10), (ushort)offset);
            strings.AddRange(records[i].Raw);
            offset += records[i].Raw.Length;
        }

        return [.. span, .. strings];
    }

    private static byte[] Os2(int length, ushort weight, ushort selection)
    {
        var os2 = new byte[length];
        if (length >= 6)
        {
            BinaryPrimitives.WriteUInt16BigEndian(os2.AsSpan(4), weight);
        }

        if (length >= 64)
        {
            BinaryPrimitives.WriteUInt16BigEndian(os2.AsSpan(62), selection);
        }

        return os2;
    }

    private static void MacStyle(Dictionary<string, byte[]> tables, ushort style) =>
        BinaryPrimitives.WriteUInt16BigEndian(tables["head"].AsSpan(44), style);

    [Fact]
    public void Outline_format_and_variations()
    {
        Assert.False(Font().IsCff);
        Assert.False(Font().IsVariable);
        Assert.True(With(t => t["CFF "] = [0]).IsCff);
        Assert.True(With(t => t["CFF2"] = [0]).IsCff);
        Assert.True(With(t => t["fvar"] = [0]).IsVariable);
    }

    [Fact]
    public void Weight_and_slant_fall_back_to_the_head_table()
    {
        Assert.Equal(500, With(t => t["OS/2"] = Os2(64, 5, 0)).Weight);
        Assert.Equal(300, With(t => t["OS/2"] = Os2(64, 300, 1)).Weight);
        Assert.True(With(t => t["OS/2"] = Os2(64, 300, 1)).Italic);
        Assert.Equal(700, With(t => { t["OS/2"] = Os2(64, 0, 0); MacStyle(t, 1); }).Weight);
        Assert.Equal(400, With(t => { t["OS/2"] = Os2(64, 2000, 0); MacStyle(t, 0); }).Weight);
        Assert.Equal(400, With(t => { t["OS/2"] = Os2(4, 0, 0); MacStyle(t, 2); }).Weight);
        Assert.True(With(t => { t["OS/2"] = Os2(10, 400, 0); MacStyle(t, 2); }).Italic);
        Assert.False(With(t => { t.Remove("OS/2"); MacStyle(t, 0); }).Italic);
        Assert.Equal(400, With(t => { t.Remove("OS/2"); t["head"] = t["head"][..20]; }).Weight);
        Assert.Equal(400, With(t => { t.Remove("OS/2"); t.Remove("head"); }).Weight);
    }

    [Fact]
    public void Collections_are_indexed_by_face()
    {
        byte[] single = Font().ToBytes();
        int numTables = BinaryPrimitives.ReadUInt16BigEndian(single.AsSpan(4));
        byte[] collection = [.. "ttcf"u8, 0, 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 16, .. single];
        for (int i = 0; i < numTables; i++)
        {
            Span<byte> offset = collection.AsSpan(16 + 12 + 16 * i + 8, 4);
            BinaryPrimitives.WriteUInt32BigEndian(offset, BinaryPrimitives.ReadUInt32BigEndian(offset) + 16);
        }

        Assert.Equal(1, SfntFont.CountFaces(collection));
        Assert.Equal(1, SfntFont.CountFaces(single));
        Assert.Equal(1, SfntFont.CountFaces("ttcf"u8));
        Assert.Equal(Font().GetName(SfntFont.NameFamily), SfntFont.Load(collection, 0).GetName(SfntFont.NameFamily));
        Assert.Throws<InvalidDataException>(() => SfntFont.Load(collection, 1));
        Assert.Throws<InvalidDataException>(() => SfntFont.Load(collection, -1));
    }

    [Fact]
    public void Broken_files_are_rejected()
    {
        Assert.Throws<InvalidDataException>(() => SfntFont.Load([1, 2, 3]));
        Assert.Throws<InvalidDataException>(() => SfntFont.Load(new byte[12]));

        byte[] bytes = Font().ToBytes();
        foreach (string tag in new[] { "OTTO", "true" })
        {
            byte[] copy = [.. bytes];
            Encoding.ASCII.GetBytes(tag, copy.AsSpan(0, 4));
            Assert.Equal(BinaryPrimitives.ReadUInt32BigEndian(copy), SfntFont.Load(copy).SfntVersion);
        }

        foreach ((int field, uint value) in new[] { (8, 0x8000_0000u), (12, 0x8000_0000u), (12, 0x0100_0000u) })
        {
            byte[] copy = [.. bytes];
            BinaryPrimitives.WriteUInt32BigEndian(copy.AsSpan(12 + field), value);
            Assert.Throws<InvalidDataException>(() => SfntFont.Load(copy));
        }
    }

    [Fact]
    public void Name_tables_tolerate_damage()
    {
        Assert.Empty(With(t => t.Remove("name")).Names);
        Assert.Empty(With(t => t["name"] = [0, 0, 0]).Names);

        byte[] table = NameTable((3, 1, 0x409, 1, Encoding.BigEndianUnicode.GetBytes("Fam")), (3, 1, 0x409, 2, Encoding.BigEndianUnicode.GetBytes("Sub")));
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(2), 5); // claims more records than fit
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(6 + 12 + 8), 200); // second string runs past the end
        SfntFont font = With(t => t["name"] = table);
        Assert.Equal("Fam", Assert.Single(font.Names).Text);
    }

    [Theory]
    [InlineData(2, "あ")] // Shift-JIS
    [InlineData(3, "中")] // GBK
    [InlineData(4, "中")] // Big5
    [InlineData(5, "한")] // Wansung
    [InlineData(6, "A")] // Johab
    public void Legacy_windows_cjk_names_are_decoded(ushort encoding, string text)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        int codePage = encoding switch { 2 => 932, 3 => 936, 4 => 950, 5 => 949, _ => 1361 };
        byte[] raw = Encoding.GetEncoding(codePage).GetBytes(text);
        byte[] padded = raw.Length == 1 ? [0, raw[0]] : raw;
        Assert.Equal(text, new NameRecord(3, encoding, 0, 1, padded).Text);
    }

    [Fact]
    public void Names_decode_and_encode_per_platform()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); // legacy code pages for the GBK record below
        Assert.Equal("Uni", new NameRecord(0, 3, 0, 1, Encoding.BigEndianUnicode.GetBytes("Uni")).Text);
        Assert.Equal("Mac", new NameRecord(1, 0, 0, 1, "Mac"u8.ToArray()).Text);
        Assert.Equal("Sym", new NameRecord(3, 0, 0, 1, Encoding.BigEndianUnicode.GetBytes("Sym")).Text);
        Assert.Equal("Odd", new NameRecord(3, 7, 0, 1, "Odd"u8.ToArray()).Text);

        SfntFont font = With(t => t["name"] = NameTable(
            (0, 3, 0, 1, Encoding.BigEndianUnicode.GetBytes("A")),
            (1, 0, 0, 1, "A"u8.ToArray()),
            (3, 3, 0, 1, Encoding.GetEncoding(936).GetBytes("中")),
            (3, 1, 0x409, 1, Encoding.BigEndianUnicode.GetBytes("A"))));
        font.ReplaceNames(new Dictionary<ushort, string> { [SfntFont.NameFamily] = "New" });
        SfntFont reloaded = SfntFont.Load(font.ToBytes());
        Assert.All(reloaded.Names, n => Assert.Equal("New", n.Text));
        Assert.Equal(["New"], reloaded.GetNames(SfntFont.NameFamily));
    }

    [Fact]
    public void Fonts_without_a_head_table_serialise()
    {
        SfntFont font = Font();
        font.Tables.Remove("head");
        Assert.False(SfntFont.Load(font.ToBytes()).Tables.ContainsKey("head"));
        font.Tables["head"] = [1, 2, 3];
        Assert.Equal([1, 2, 3], SfntFont.Load(font.ToBytes()).Tables["head"]);
    }
}
