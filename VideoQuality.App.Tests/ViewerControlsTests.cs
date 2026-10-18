using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using VideoQuality.App.Controls;
using VideoQuality.App.ViewModels;
using VideoQuality.Core.Decoding;

namespace VideoQuality.App.Tests;

public class ViewerControlsTests
{
    /// <summary>An image that reports a size but draws nothing (for sizes a real bitmap cannot have).</summary>
    private sealed class SizedImage(double width, double height) : IImage
    {
        public Size Size => new(width, height);

        public void Draw(DrawingContext context, Rect sourceRect, Rect destRect)
        {
        }
    }

    private static WriteableBitmap Bitmap(int width, int height) =>
        new(new PixelSize(width, height), new Vector(96, 96), Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Opaque);

    private static void RenderDetached(Control control, double width, double height)
    {
        control.Measure(new Size(width, height));
        control.Arrange(new Rect(0, 0, width, height));
        using var target = new RenderTargetBitmap(new PixelSize(64, 64));
        using DrawingContext context = target.CreateDrawingContext();
        control.Render(context);
    }

    [Fact]
    public void Placement_snaps_to_physical_pixels_and_picks_sampling()
    {
        Rect snapped = ImagePlacement.Destination(new Size(101, 101), new Size(10, 10), 1, 0.5, 0.5, 1.5);
        Assert.Equal(Math.Round(45.5 * 1.5) / 1.5, snapped.X, 6);
        Rect raw = ImagePlacement.Destination(new Size(101, 101), new Size(10, 10), 1, 0.5, 0.5, 0);
        Assert.Equal(45.5, raw.X);
        Assert.Equal(BitmapInterpolationMode.None, ImagePlacement.Sampling(1, 1).BitmapInterpolationMode);
        Assert.Equal(BitmapInterpolationMode.HighQuality, ImagePlacement.Sampling(0.5, 1).BitmapInterpolationMode);
        Assert.Equal(BitmapInterpolationMode.HighQuality, ImagePlacement.Sampling(1, 0).BitmapInterpolationMode);
    }

    [Fact]
    public void Effective_scale_fits_or_follows_the_zoom()
    {
        Assert.Equal(1, ImagePlacement.EffectiveScale(null, 0, new Size(100, 100)));
        Assert.Equal(1, ImagePlacement.EffectiveScale(new SizedImage(0, 10), 0, new Size(100, 100)));
        Assert.Equal(1, ImagePlacement.EffectiveScale(new SizedImage(10, 0), 0, new Size(100, 100)));
        Assert.Equal(2, ImagePlacement.EffectiveScale(new SizedImage(10, 10), 2, new Size(100, 100)));
        Assert.Equal(1, ImagePlacement.EffectiveScale(new SizedImage(10, 10), 0, new Size(0, 100)));
        Assert.Equal(1, ImagePlacement.EffectiveScale(new SizedImage(10, 10), 0, new Size(100, 0)));
        Assert.Equal(5, ImagePlacement.EffectiveScale(new SizedImage(10, 20), 0, new Size(100, 100)));
    }

    [AvaloniaFact]
    public void Frame_and_wipe_views_draw_in_every_state()
    {
        var frame = new FrameView { Source = Bitmap(8, 8), Zoom = 2, CenterX = 0.2, CenterY = 0.8 };
        var wipe = new WipeView { SourceA = Bitmap(8, 8), SourceB = Bitmap(8, 8), Zoom = 0, CenterX = 0.5, CenterY = 0.5, Split = 0.3 };
        var wipeAlone = new WipeView { SourceA = Bitmap(8, 8) };
        Assert.Equal((2, 0.2, 0.8), (frame.Zoom, frame.CenterX, frame.CenterY));
        Assert.Equal((0, 0.5, 0.5, 0.3), (wipe.Zoom, wipe.CenterX, wipe.CenterY, wipe.Split));
        Assert.NotNull(wipe.SourceB);

        Window window = Ui.Show(new StackPanel { Children = { new Border { Width = 200, Height = 100, Child = frame }, new Border { Width = 200, Height = 100, Child = wipe }, new Border { Width = 200, Height = 100, Child = wipeAlone } } }, null);
        Assert.NotNull(Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window));
        Assert.Equal(2, frame.GetEffectiveScale());
        Assert.Equal(Math.Min(200 / 8.0, 100 / 8.0), wipe.GetEffectiveScale());
        window.Close();

        // Outside a window, without a source, at zero size, with an empty image.
        RenderDetached(new FrameView { Source = Bitmap(8, 8) }, 32, 32);
        RenderDetached(new FrameView(), 32, 32);
        RenderDetached(new FrameView { Source = Bitmap(8, 8) }, 0, 0);
        RenderDetached(new FrameView { Source = Bitmap(8, 8) }, 10, 0);
        RenderDetached(new FrameView { Source = new SizedImage(0, 0) }, 32, 32);
        RenderDetached(new WipeView { SourceA = Bitmap(8, 8), Split = 2 }, 32, 32);
        RenderDetached(new WipeView(), 32, 32);
        RenderDetached(new WipeView { SourceA = Bitmap(8, 8) }, 0, 32);
        RenderDetached(new WipeView { SourceA = Bitmap(8, 8) }, 32, 0);
    }

    [AvaloniaFact]
    public void Swap_chain_reuses_two_bitmaps_and_copies_padded_rows()
    {
        using var chain = new BitmapSwapChain();
        var tight = new DecodedFrame(4, 2, 16, new byte[32], 0);
        WriteableBitmap first = chain.Present(tight);
        WriteableBitmap second = chain.Present(tight);
        Assert.NotSame(first, second);
        Assert.Same(first, chain.Present(tight)); // reused

        var padded = new DecodedFrame(4, 2, 24, new byte[48], 0); // stride wider than the bitmap's rows
        WriteableBitmap resized = chain.Present(padded);
        Assert.Equal(new PixelSize(4, 2), resized.PixelSize);
        WriteableBitmap bigger = chain.Present(new DecodedFrame(8, 2, 32, new byte[64], 0));
        Assert.Equal(new PixelSize(8, 2), bigger.PixelSize);
        chain.Dispose();
        chain.Dispose();
    }

    [Fact]
    public void Viewport_fits_zooms_at_the_cursor_and_pans()
    {
        var viewport = new ViewportState();
        Assert.True(viewport.IsFit);
        viewport.ActualSize(1.5);
        Assert.Equal(1 / 1.5, viewport.Zoom, 6);
        Assert.False(viewport.IsFit);
        viewport.ActualSize(0);
        Assert.Equal(100, viewport.Zoom);
        viewport.Fit();
        Assert.True(viewport.IsFit);

        viewport.ZoomAt(100, 50, 200, 100, 400, 200, 0.5, 2); // at the centre: the centre stays
        Assert.Equal((1.0, 0.5, 0.5), (viewport.Zoom, viewport.CenterX, viewport.CenterY));
        viewport.ZoomAt(0, 0, 200, 100, 400, 200, 1, 1000); // clamped
        Assert.Equal(16, viewport.Zoom);
        foreach ((double vw, double vh, double iw, double ih) in new[] { (0.0, 1.0, 1.0, 1.0), (1.0, 0.0, 1.0, 1.0), (1.0, 1.0, 0.0, 1.0), (1.0, 1.0, 1.0, 0.0) })
        {
            viewport.ZoomAt(0, 0, vw, vh, iw, ih, 1, 2);
        }

        Assert.Equal(16, viewport.Zoom);

        viewport.Fit();
        viewport.Pan(100, -50, 400, 200, 1);
        Assert.Equal((0.25, 0.75), (viewport.CenterX, viewport.CenterY));
        viewport.Pan(1, 1, 0, 200, 1);
        viewport.Pan(1, 1, 400, 0, 1);
        viewport.Pan(1, 1, 400, 200, 0);
        Assert.Equal((0.25, 0.75), (viewport.CenterX, viewport.CenterY));
    }
}
