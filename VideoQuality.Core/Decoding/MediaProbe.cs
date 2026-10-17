using FFmpeg.AutoGen;

namespace VideoQuality.Core.Decoding;

/// <summary>Stream properties that can be read without opening a decoder.</summary>
public sealed record MediaProbeResult(int Width, int Height, string PixelFormat, int BitDepth);

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
            int result = ffmpeg.avformat_open_input(&format, path, null, null);
            if (result < 0)
            {
                throw new InvalidOperationException($"Could not open '{path}': {FfmpegRuntime.ErrorText(result)}");
            }

            result = ffmpeg.avformat_find_stream_info(format, null);
            if (result < 0)
            {
                throw new InvalidOperationException($"Could not read stream info for '{path}': {FfmpegRuntime.ErrorText(result)}");
            }

            int streamIndex = ffmpeg.av_find_best_stream(format, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
            if (streamIndex < 0)
            {
                throw new InvalidOperationException($"'{path}' has no video stream.");
            }

            AVCodecParameters* parameters = format->streams[streamIndex]->codecpar;
            string pixelFormat = ffmpeg.av_get_pix_fmt_name((AVPixelFormat)parameters->format) ?? string.Empty;

            return new MediaProbeResult(
                parameters->width,
                parameters->height,
                pixelFormat,
                FrameDecoder.ResolveBitDepth(parameters, pixelFormat));
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
