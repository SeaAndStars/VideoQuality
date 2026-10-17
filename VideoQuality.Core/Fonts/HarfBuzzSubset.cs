using System.Runtime.InteropServices;

namespace VideoQuality.Core.Fonts;

/// <summary>
/// Glyph subsetting through HarfBuzz's hb-subset (MIT), called directly in the libHarfBuzzSharp
/// native library that Avalonia/SkiaSharp already ship. Only the handful of functions needed here
/// are bound.
/// </summary>
public static partial class HarfBuzzSubset
{
    private const string Library = "libHarfBuzzSharp";

    // hb_subset_sets_t
    private const int SetNameId = 4;
    private const int SetNameLanguage = 5;
    private const int SetLayoutFeature = 6;

    /// <summary>
    /// Subsets one face to the given code points. All OpenType layout features (vertical forms,
    /// ligatures, kerning) and all name records are kept, so the subset renders like the original.
    /// </summary>
    public static byte[] Subset(byte[] fontFile, int faceIndex, IEnumerable<int> codepoints)
    {
        GCHandle pin = GCHandle.Alloc(fontFile, GCHandleType.Pinned);
        IntPtr blob = IntPtr.Zero, face = IntPtr.Zero, input = IntPtr.Zero, subset = IntPtr.Zero, result = IntPtr.Zero;
        try
        {
            // DUPLICATE: HarfBuzz copies the bytes, so the pin can end with this call.
            blob = hb_blob_create(pin.AddrOfPinnedObject(), (uint)fontFile.Length, 0, IntPtr.Zero, IntPtr.Zero);
            face = hb_face_create(blob, (uint)faceIndex);
            input = Require(hb_subset_input_create_or_fail(), "hb_subset_input_create_or_fail failed.");

            IntPtr unicodes = hb_subset_input_unicode_set(input);
            foreach (int codepoint in codepoints)
            {
                hb_set_add(unicodes, (uint)codepoint);
            }

            foreach (int set in (int[])[SetLayoutFeature, SetNameId, SetNameLanguage])
            {
                IntPtr handle = hb_subset_input_set(input, set);
                hb_set_clear(handle);
                hb_set_invert(handle); // empty → everything
            }

            subset = Require(hb_subset_or_fail(face, input), "HarfBuzz could not subset this font.");
            result = hb_face_reference_blob(subset);

            // An empty blob has no data pointer.
            IntPtr data = Require(hb_blob_get_data(result, out uint length), "HarfBuzz produced an empty font.");

            var bytes = new byte[length];
            Marshal.Copy(data, bytes, 0, (int)length);
            return bytes;
        }
        finally
        {
            if (result != IntPtr.Zero) hb_blob_destroy(result);
            if (subset != IntPtr.Zero) hb_face_destroy(subset);
            if (input != IntPtr.Zero) hb_subset_input_destroy(input);
            if (face != IntPtr.Zero) hb_face_destroy(face);
            if (blob != IntPtr.Zero) hb_blob_destroy(blob);
            pin.Free();
        }
    }

    internal static IntPtr Require(IntPtr handle, string error) =>
        handle != IntPtr.Zero ? handle : throw new InvalidOperationException(error);

    /// <summary>Code points the face has no glyph for (cmap), used to verify a subset.</summary>
    public static HashSet<int> MissingGlyphs(byte[] fontFile, int faceIndex, IEnumerable<int> codepoints)
    {
        GCHandle pin = GCHandle.Alloc(fontFile, GCHandleType.Pinned);
        IntPtr blob = IntPtr.Zero, face = IntPtr.Zero, font = IntPtr.Zero;
        var missing = new HashSet<int>();
        try
        {
            blob = hb_blob_create(pin.AddrOfPinnedObject(), (uint)fontFile.Length, 0, IntPtr.Zero, IntPtr.Zero);
            face = hb_face_create(blob, (uint)faceIndex);
            font = hb_font_create(face);
            foreach (int codepoint in codepoints)
            {
                if (!hb_font_get_nominal_glyph(font, (uint)codepoint, out _))
                {
                    missing.Add(codepoint);
                }
            }

            return missing;
        }
        finally
        {
            if (font != IntPtr.Zero) hb_font_destroy(font);
            if (face != IntPtr.Zero) hb_face_destroy(face);
            if (blob != IntPtr.Zero) hb_blob_destroy(blob);
            pin.Free();
        }
    }

    public static string Version => string.Concat(Marshal.PtrToStringAnsi(hb_version_string()), string.Empty);

    [LibraryImport(Library)] private static partial IntPtr hb_version_string();
    [LibraryImport(Library)] private static partial IntPtr hb_blob_create(IntPtr data, uint length, int mode, IntPtr userData, IntPtr destroy);
    [LibraryImport(Library)] private static partial void hb_blob_destroy(IntPtr blob);
    [LibraryImport(Library)] private static partial IntPtr hb_blob_get_data(IntPtr blob, out uint length);
    [LibraryImport(Library)] private static partial IntPtr hb_face_create(IntPtr blob, uint index);
    [LibraryImport(Library)] private static partial void hb_face_destroy(IntPtr face);
    [LibraryImport(Library)] private static partial IntPtr hb_face_reference_blob(IntPtr face);
    [LibraryImport(Library)] private static partial IntPtr hb_font_create(IntPtr face);
    [LibraryImport(Library)] private static partial void hb_font_destroy(IntPtr font);
    [LibraryImport(Library)] [return: MarshalAs(UnmanagedType.I4)] private static partial bool hb_font_get_nominal_glyph(IntPtr font, uint unicode, out uint glyph);
    [LibraryImport(Library)] private static partial IntPtr hb_subset_input_create_or_fail();
    [LibraryImport(Library)] private static partial void hb_subset_input_destroy(IntPtr input);
    [LibraryImport(Library)] private static partial IntPtr hb_subset_input_unicode_set(IntPtr input);
    [LibraryImport(Library)] private static partial IntPtr hb_subset_input_set(IntPtr input, int setType);
    [LibraryImport(Library)] private static partial IntPtr hb_subset_or_fail(IntPtr source, IntPtr input);
    [LibraryImport(Library)] private static partial void hb_set_add(IntPtr set, uint codepoint);
    [LibraryImport(Library)] private static partial void hb_set_clear(IntPtr set);
    [LibraryImport(Library)] private static partial void hb_set_invert(IntPtr set);
}
