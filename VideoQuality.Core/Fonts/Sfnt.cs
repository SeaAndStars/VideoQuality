using System.Buffers.Binary;
using System.Text;

namespace VideoQuality.Core.Fonts;

/// <summary>One record of an OpenType 'name' table, with its raw bytes and decoded text.</summary>
public sealed record NameRecord(ushort PlatformId, ushort EncodingId, ushort LanguageId, ushort NameId, byte[] Raw)
{
    public string Text => SfntFont.DecodeName(PlatformId, EncodingId, Raw);
}

/// <summary>
/// Minimal OpenType/TrueType container access (written from the OpenType specification): table
/// directory, 'name', weight and slant, and rebuilding a font with a replaced 'name' table.
/// </summary>
public sealed class SfntFont
{
    public const ushort NameCopyright = 0;
    public const ushort NameFamily = 1;
    public const ushort NameSubfamily = 2;
    public const ushort NameFull = 4;
    public const ushort NamePostScript = 6;
    public const ushort NameTypographicFamily = 16;
    public const ushort NameTypographicSubfamily = 17;

    private SfntFont(uint sfntVersion, Dictionary<string, byte[]> tables)
    {
        SfntVersion = sfntVersion;
        Tables = tables;
        Names = ReadNames(tables.GetValueOrDefault("name"));
    }

    public uint SfntVersion { get; }

    /// <summary>Tables by tag ("name", "OS/2", "glyf", "CFF ", …), as raw bytes.</summary>
    public Dictionary<string, byte[]> Tables { get; }

    public IReadOnlyList<NameRecord> Names { get; private set; }

    public bool IsCff => Tables.ContainsKey("CFF ") || Tables.ContainsKey("CFF2");

    public bool IsVariable => Tables.ContainsKey("fvar");

    /// <summary>OS/2 usWeightClass, falling back to head.macStyle (700 bold / 400 regular).</summary>
    public int Weight
    {
        get
        {
            if (Tables.TryGetValue("OS/2", out byte[]? os2) && os2.Length >= 6)
            {
                int weight = BinaryPrimitives.ReadUInt16BigEndian(os2.AsSpan(4));
                if (weight is >= 1 and <= 1000)
                {
                    return weight < 10 ? weight * 100 : weight; // some old fonts store 1–9
                }
            }

            return (MacStyle & 1) != 0 ? 700 : 400;
        }
    }

    public bool Italic
    {
        get
        {
            if (Tables.TryGetValue("OS/2", out byte[]? os2) && os2.Length >= 64)
            {
                return (BinaryPrimitives.ReadUInt16BigEndian(os2.AsSpan(62)) & 1) != 0;
            }

            return (MacStyle & 2) != 0;
        }
    }

    private int MacStyle => Tables.TryGetValue("head", out byte[]? head) && head.Length >= 46
        ? BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(44))
        : 0;

    /// <summary>All decoded strings for a name ID, English (Windows 0x409, Mac 0) first.</summary>
    public IEnumerable<string> GetNames(ushort nameId) => Names
        .Where(n => n.NameId == nameId)
        .OrderBy(n => n.PlatformId == 3 && n.LanguageId == 0x409 ? 0 : n.PlatformId == 1 && n.LanguageId == 0 ? 1 : 2)
        .Select(n => n.Text.Trim())
        .Where(t => t.Length > 0)
        .Distinct(StringComparer.Ordinal);

    public string? GetName(ushort nameId) => GetNames(nameId).FirstOrDefault();

    // ---------------------------------------------------------------- reading

    /// <summary>Number of faces in a file: 1 for TTF/OTF, numFonts for a TTC/OTC collection.</summary>
    public static int CountFaces(ReadOnlySpan<byte> file) =>
        file.Length >= 12 && file[..4].SequenceEqual("ttcf"u8)
            ? (int)BinaryPrimitives.ReadUInt32BigEndian(file[8..])
            : 1;

    public static SfntFont Load(byte[] file, int faceIndex = 0)
    {
        int offset = 0;
        if (file.Length >= 12 && file.AsSpan(0, 4).SequenceEqual("ttcf"u8))
        {
            int count = (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(8));
            if (faceIndex < 0 || faceIndex >= count)
            {
                throw new InvalidDataException($"Face {faceIndex} is out of range (collection has {count}).");
            }

            offset = (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(12 + 4 * faceIndex));
        }

        if (file.Length < offset + 12)
        {
            throw new InvalidDataException("Not an OpenType font.");
        }

        uint version = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(offset));
        if (version is not (0x00010000 or 0x4F54544F /* OTTO */ or 0x74727565 /* true */))
        {
            throw new InvalidDataException($"Unsupported font format 0x{version:X8}.");
        }

        int numTables = BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(offset + 4));
        var tables = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        for (int i = 0; i < numTables; i++)
        {
            int record = offset + 12 + 16 * i;
            string tag = Encoding.ASCII.GetString(file, record, 4);
            int tableOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(record + 8));
            int length = (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(record + 12));
            if (tableOffset < 0 || length < 0 || (long)tableOffset + length > file.Length)
            {
                throw new InvalidDataException($"Table '{tag}' lies outside the file.");
            }

            tables[tag] = file.AsSpan(tableOffset, length).ToArray();
        }

        return new SfntFont(version, tables);
    }

    private static List<NameRecord> ReadNames(byte[]? table)
    {
        var records = new List<NameRecord>();
        if (table is null || table.Length < 6)
        {
            return records;
        }

        int count = BinaryPrimitives.ReadUInt16BigEndian(table.AsSpan(2));
        int stringOffset = BinaryPrimitives.ReadUInt16BigEndian(table.AsSpan(4));
        for (int i = 0; i < count; i++)
        {
            int r = 6 + 12 * i;
            if (r + 12 > table.Length)
            {
                break;
            }

            ushort platform = BinaryPrimitives.ReadUInt16BigEndian(table.AsSpan(r));
            ushort encoding = BinaryPrimitives.ReadUInt16BigEndian(table.AsSpan(r + 2));
            ushort language = BinaryPrimitives.ReadUInt16BigEndian(table.AsSpan(r + 4));
            ushort nameId = BinaryPrimitives.ReadUInt16BigEndian(table.AsSpan(r + 6));
            int length = BinaryPrimitives.ReadUInt16BigEndian(table.AsSpan(r + 8));
            int offset = BinaryPrimitives.ReadUInt16BigEndian(table.AsSpan(r + 10));
            int start = stringOffset + offset;
            if (start + length <= table.Length)
            {
                records.Add(new NameRecord(platform, encoding, language, nameId, table.AsSpan(start, length).ToArray()));
            }
        }

        return records;
    }

    /// <summary>
    /// Decodes a name string: UTF-16BE for Unicode and Windows Unicode records; legacy Windows CJK
    /// encodings (common in older Chinese and Japanese fonts) and Mac Roman otherwise.
    /// </summary>
    internal static string DecodeName(ushort platform, ushort encoding, byte[] raw)
    {
        if (platform == 0 || (platform == 3 && encoding is 0 or 1 or 10))
        {
            return Encoding.BigEndianUnicode.GetString(raw);
        }

        if (platform == 3 && encoding is >= 2 and <= 6)
        {
            // Stored as 16-bit units; multi-byte characters keep both bytes, single bytes are padded.
            int codePage = encoding switch { 2 => 932, 3 => 936, 4 => 950, 5 => 949, _ => 1361 };
            byte[] packed = raw.Where(b => b != 0).ToArray();
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(codePage).GetString(packed);
        }

        return Encoding.Latin1.GetString(raw); // Mac Roman: ASCII-compatible for real font names
    }

    // ---------------------------------------------------------------- writing

    /// <summary>
    /// Replaces the strings of the given name IDs in every platform/language record. Other records
    /// (copyright, license, designer, …) keep their original bytes.
    /// </summary>
    public void ReplaceNames(IReadOnlyDictionary<ushort, string> replacements)
    {
        var records = Names
            .Select(n => replacements.TryGetValue(n.NameId, out string? value)
                ? n with { Raw = EncodeName(n.PlatformId, value) }
                : n)
            .ToList();

        // Fonts without a record for a replaced ID (e.g. no typographic family) need none added:
        // renderers fall back to ID 1/2 when 16/17 are absent.
        Names = records;
        Tables["name"] = WriteNameTable(records);
    }

    // Unicode and every Windows record (legacy CJK ones read ASCII in 16-bit units) take UTF-16BE;
    // Mac records take ASCII. Replacement names are always ASCII.
    private static byte[] EncodeName(ushort platform, string value) =>
        platform is 0 or 3 ? Encoding.BigEndianUnicode.GetBytes(value) : Encoding.ASCII.GetBytes(value);

    private static byte[] WriteNameTable(List<NameRecord> records)
    {
        records = records
            .OrderBy(r => r.PlatformId).ThenBy(r => r.EncodingId).ThenBy(r => r.LanguageId).ThenBy(r => r.NameId)
            .ToList();

        int headerSize = 6 + 12 * records.Count;
        using var strings = new MemoryStream();
        var table = new byte[headerSize];
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(0), 0);
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(2), (ushort)records.Count);
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(4), (ushort)headerSize);

        for (int i = 0; i < records.Count; i++)
        {
            NameRecord n = records[i];
            int r = 6 + 12 * i;
            BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(r), n.PlatformId);
            BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(r + 2), n.EncodingId);
            BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(r + 4), n.LanguageId);
            BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(r + 6), n.NameId);
            BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(r + 8), (ushort)n.Raw.Length);
            BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(r + 10), (ushort)strings.Position);
            strings.Write(n.Raw);
        }

        return [.. table, .. strings.ToArray()];
    }

    /// <summary>
    /// Serialises a single font: table directory sorted by tag, 4-byte aligned tables, per-table
    /// checksums and head.checkSumAdjustment, as the OpenType specification requires.
    /// </summary>
    public byte[] ToBytes()
    {
        var tags = Tables.Keys.OrderBy(t => t, StringComparer.Ordinal).ToList();
        int numTables = tags.Count;
        int entrySelector = (int)Math.Floor(Math.Log2(numTables));
        int searchRange = (1 << entrySelector) * 16;

        if (Tables.TryGetValue("head", out byte[]? head) && head.Length >= 12)
        {
            BinaryPrimitives.WriteUInt32BigEndian(head.AsSpan(8), 0); // checksum computed with zero
        }

        int directorySize = 12 + 16 * numTables;
        int total = directorySize + tags.Sum(t => Align4(Tables[t].Length));
        var output = new byte[total];

        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(0), SfntVersion);
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(4), (ushort)numTables);
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(6), (ushort)searchRange);
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(8), (ushort)entrySelector);
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(10), (ushort)(numTables * 16 - searchRange));

        int offset = directorySize;
        int headOffset = -1;
        for (int i = 0; i < numTables; i++)
        {
            string tag = tags[i];
            byte[] data = Tables[tag];
            int record = 12 + 16 * i;
            Encoding.ASCII.GetBytes(tag, output.AsSpan(record, 4));
            BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(record + 4), Checksum(data));
            BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(record + 8), (uint)offset);
            BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(record + 12), (uint)data.Length);
            data.CopyTo(output, offset);
            if (tag == "head" && data.Length >= 12)
            {
                headOffset = offset; // a truncated head has no checksum field to adjust
            }

            offset += Align4(data.Length);
        }

        if (headOffset >= 0)
        {
            uint adjustment = unchecked(0xB1B0AFBA - Checksum(output));
            BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(headOffset + 8), adjustment);
            BinaryPrimitives.WriteUInt32BigEndian(Tables["head"].AsSpan(8), adjustment);
        }

        return output;
    }

    internal static uint Checksum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        int i = 0;
        for (; i + 4 <= data.Length; i += 4)
        {
            sum = unchecked(sum + BinaryPrimitives.ReadUInt32BigEndian(data[i..]));
        }

        if (i < data.Length)
        {
            Span<byte> tail = stackalloc byte[4];
            tail.Clear();
            data[i..].CopyTo(tail);
            sum = unchecked(sum + BinaryPrimitives.ReadUInt32BigEndian(tail));
        }

        return sum;
    }

    private static int Align4(int length) => (length + 3) & ~3;
}
