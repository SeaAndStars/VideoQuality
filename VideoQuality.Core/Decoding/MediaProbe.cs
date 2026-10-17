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
            int result = ffmpeg.avformat_open_input(&format, path, null, null);
            if (result < 0)
            {
                throw new InvalidOperationException(Loc.T("probe.error.open", path, FfmpegRuntime.ErrorText(result)));
            }

            result = ffmpeg.avformat_find_stream_info(format, null);
            if (result < 0)
            {
                throw new InvalidOperationException(Loc.T("probe.error.streamInfo", path, FfmpegRuntime.ErrorText(result)));
            }

            int streamIndex = ffmpeg.av_find_best_stream(format, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
            if (streamIndex < 0)
            {
                throw new InvalidOperationException(Loc.T("probe.error.noVideo", path));
            }

            AVStream* stream = format->streams[streamIndex];
            AVCodecParameters* parameters = stream->codecpar;
            AVRational rate = stream->avg_frame_rate.num != 0 && stream->avg_frame_rate.den != 0
                ? stream->avg_frame_rate
                : stream->r_frame_rate;
            string pixelFormat = ffmpeg.av_get_pix_fmt_name((AVPixelFormat)parameters->format) ?? string.Empty;

            return new MediaProbeResult(
                parameters->width,
                parameters->height,
                pixelFormat,
                FrameDecoder.ResolveBitDepth(parameters, pixelFormat))
            {
                FrameRate = rate.den != 0 ? ffmpeg.av_q2d(rate) : 0,
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
