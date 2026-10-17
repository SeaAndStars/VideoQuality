namespace VideoQuality.Core.Decoding;

/// <summary>A decoded video frame converted to BGRA8888 (premultiplied-free, opaque).</summary>
/// <param name="Width">Frame width in pixels.</param>
/// <param name="Height">Frame height in pixels.</param>
/// <param name="Stride">Bytes per row (always <c>Width * 4</c>).</param>
/// <param name="Bgra">Packed BGRA pixels, top-down.</param>
/// <param name="Pts">Presentation timestamp in stream time base units, for diagnostics.</param>
public sealed record DecodedFrame(int Width, int Height, int Stride, byte[] Bgra, long Pts);
