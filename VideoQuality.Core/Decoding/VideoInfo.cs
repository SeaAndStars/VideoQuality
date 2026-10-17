using VideoQuality.Core.Localization;

namespace VideoQuality.Core.Decoding;

/// <summary>Basic properties of a decoded video stream.</summary>
public sealed record VideoInfo(
    int Width,
    int Height,
    double FrameRate,
    int FrameCount,
    string Codec,
    TimeSpan Duration)
{
    /// <summary>Pixel format name, e.g. <c>yuv420p</c> or <c>yuv420p10le</c>.</summary>
    public string PixelFormat { get; init; } = string.Empty;

    /// <summary>Bits per component, derived from the pixel format (8, 10, 12, 16…).</summary>
    public int BitDepth { get; init; } = 8;

    /// <summary>Declared matrix, e.g. <c>bt709</c>, or empty when unspecified.</summary>
    public string ColorSpace { get; init; } = string.Empty;

    /// <summary>Declared range, <c>tv</c> or <c>pc</c>, or empty when unspecified.</summary>
    public string ColorRange { get; init; } = string.Empty;

    /// <summary>True for delivery codecs (H.264/HEVC/AV1/…); false for lossless ones.</summary>
    public bool IsLossy { get; init; }

    public bool HasKnownFrameCount => FrameCount > 0;

    public bool IsHighBitDepth => BitDepth > 8;

    /// <summary>Compact label for the pane header, e.g. <c>10-bit</c> or <c>lossy</c>.</summary>
    public string Badge
    {
        get
        {
            var parts = new List<string>(2);
            if (IsHighBitDepth)
            {
                parts.Add($"{BitDepth}-bit");
            }

            if (IsLossy)
            {
                parts.Add(Loc.T("video.badge.lossy"));
            }

            return string.Join(" · ", parts);
        }
    }

    public override string ToString() =>
        $"{Width}x{Height} @ {FrameRate:0.###} fps, {FrameCount} frames, {Codec}, {Duration:hh\\:mm\\:ss\\.fff}";
}
