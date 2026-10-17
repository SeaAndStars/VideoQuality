using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using VideoQuality.Core.Decoding;

namespace VideoQuality.App.ViewModels;

/// <summary>
/// Two reusable bitmaps for one pane. Each frame is copied into the bitmap that is not on screen,
/// which is then returned for binding, so playback does not allocate (and leak until finalisation)
/// a new 8 MB native bitmap per pane per frame.
/// </summary>
internal sealed class BitmapSwapChain : IDisposable
{
    private readonly WriteableBitmap?[] _buffers = new WriteableBitmap?[2];
    private int _next;

    public WriteableBitmap Present(DecodedFrame frame)
    {
        var size = new PixelSize(frame.Width, frame.Height);
        WriteableBitmap? bitmap = _buffers[_next];

        if (bitmap is null || bitmap.PixelSize != size)
        {
            // The other buffer is the one on screen; this one is free to replace.
            bitmap?.Dispose();
            bitmap = new WriteableBitmap(size, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            _buffers[_next] = bitmap;
        }

        using (ILockedFramebuffer buffer = bitmap.Lock())
        {
            if (buffer.RowBytes == frame.Stride)
            {
                Marshal.Copy(frame.Bgra, 0, buffer.Address, frame.Bgra.Length);
            }
            else
            {
                for (int y = 0; y < frame.Height; y++)
                {
                    Marshal.Copy(
                        frame.Bgra,
                        y * frame.Stride,
                        IntPtr.Add(buffer.Address, y * buffer.RowBytes),
                        Math.Min(frame.Stride, buffer.RowBytes));
                }
            }
        }

        _next ^= 1;
        return bitmap;
    }

    public void Dispose()
    {
        foreach (WriteableBitmap? bitmap in _buffers)
        {
            bitmap?.Dispose();
        }

        Array.Clear(_buffers);
    }
}
