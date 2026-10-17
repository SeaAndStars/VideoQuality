using FFmpeg.AutoGen;
using VideoQuality.Core.Localization;

namespace VideoQuality.Core.Decoding;

/// <summary>Stream properties that can be read without opening a decoder.</summary>
public sealed record MediaProbeResult(int Width, int Height, string PixelFormat, int BitDepth)
{
    /// <summary>Average frame rate, or 0 when the container does not declare one.</summary>
    public double FrameRate { get; init; }
}

/// <summary>
/// Reads codec parameters only: enough to know resolution, pixel format and bit depth for reporting
/// and for choosing a scoring path. Does not decode anything.
/// </summary>
public static unsafe class MediaProbe
{
    public static MediaProbeResult Probe(string path)
    {
        FfmpegRuntime.Initialize();

        AVFormatContext* format = null;
        try
        {
            FrameDecoder.Check(ffmpeg.avformat_open_input(&format, path, null, null), "probe.error.open", path);
            FrameDecoder.Check(ffmpeg.avformat_find_stream_info(format, null), "probe.error.streamInfo", path);
            int streamIndex = ffmpeg.av_find_best_stream(format, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
            FrameDecoder.Ensure(streamIndex >= 0, "probe.error.noVideo", path);

            AVStream* stream = format->streams[streamIndex];
            AVCodecParameters* parameters = stream->codecpar;
            string pixelFormat = $"{ffmpeg.av_get_pix_fmt_name((AVPixelFormat)parameters->format)}";

            return new MediaProbeResult(
                parameters->width,
                parameters->height,
                pixelFormat,
                FrameDecoder.ResolveBitDepth(parameters->bits_per_raw_sample, pixelFormat))
            {
                FrameRate = FrameDecoder.ResolveFrameRate(stream->avg_frame_rate, stream->r_frame_rate),
            };
        }
        finally
        {
            if (format != null)
            {
                ffmpeg.avformat_close_input(&format);
            }
        }
    }
}
