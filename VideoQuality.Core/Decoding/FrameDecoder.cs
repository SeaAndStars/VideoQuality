using System.Globalization;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace VideoQuality.Core.Decoding;

/// <summary>
/// Decodes a video to BGRA frames through FFmpeg.AutoGen.
/// </summary>
/// <remarks>
/// <para>
/// Seeking is exact: the decoder jumps to the keyframe at or before the requested frame, then
/// decodes forward until the frame's timestamp matches. A small LRU cache makes stepping backwards
/// and forwards cheap for the usual ±a-few-frames case.
/// </para>
/// <para>
/// One instance is not thread safe. The UI serialises calls through a semaphore.
/// </para>
/// </remarks>
public sealed unsafe class FrameDecoder : IDisposable
{
    private const int CacheCapacity = 8;
    private const int MaxForwardDecode = 240;
    private const int AVSEEK_FLAG_BACKWARD = 1;
    private const int AVERROR_EOF = -541478725;

    private readonly object _gate = new();
    private readonly Dictionary<int, DecodedFrame> _cache = new();
    private readonly LinkedList<int> _lru = new();

    private AVFormatContext* _format;
    private AVCodecContext* _codec;
    private SwsContext* _sws;
    private AVFrame* _frame;
    private AVPacket* _packet;
    private int _swsWidth;
    private int _swsHeight;
    private int _swsFormat = -1;
    private int _lastIndex = -1;
    private int _streamIndex;
    private AVRational _timeBase;
    private double _fps;
    private double _timeBaseSeconds;
    private bool _draining;
    private bool _disposed;

    private FrameDecoder(string path)
    {
        Path = path;
        FfmpegRuntime.Initialize();

        AVFormatContext* format = null;
        try
        {
            int result = ffmpeg.avformat_open_input(&format, path, null, null);
            if (result < 0)
            {
                throw new InvalidOperationException($"Could not open '{path}': {FfmpegRuntime.ErrorText(result)}");
            }

            _format = format;

            result = ffmpeg.avformat_find_stream_info(_format, null);
            if (result < 0)
            {
                throw new InvalidOperationException($"Could not read stream info for '{path}': {FfmpegRuntime.ErrorText(result)}");
            }

            _streamIndex = ffmpeg.av_find_best_stream(_format, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
            if (_streamIndex < 0)
            {
                throw new InvalidOperationException($"'{path}' has no decodable video stream.");
            }

            AVStream* stream = _format->streams[_streamIndex];
            AVCodecParameters* codecParameters = stream->codecpar;
            AVCodec* codec = ffmpeg.avcodec_find_decoder(codecParameters->codec_id);
            if (codec == null)
            {
                throw new InvalidOperationException(
                    $"No decoder found for codec '{ffmpeg.avcodec_get_name(codecParameters->codec_id)}'.");
            }

            _codec = ffmpeg.avcodec_alloc_context3(codec);
            if (_codec == null)
            {
                throw new OutOfMemoryException("avcodec_alloc_context3 failed.");
            }

            result = ffmpeg.avcodec_parameters_to_context(_codec, codecParameters);
            if (result < 0)
            {
                throw new InvalidOperationException($"Could not copy codec parameters: {FfmpegRuntime.ErrorText(result)}");
            }

            _codec->pkt_timebase = stream->time_base;
            _codec->thread_count = 0; // let FFmpeg pick a thread count

            result = ffmpeg.avcodec_open2(_codec, codec, null);
            if (result < 0)
            {
                throw new InvalidOperationException($"Could not open decoder: {FfmpegRuntime.ErrorText(result)}");
            }

            _frame = ffmpeg.av_frame_alloc();
            _packet = ffmpeg.av_packet_alloc();
            if (_frame == null || _packet == null)
            {
                throw new OutOfMemoryException("Could not allocate FFmpeg frame or packet.");
            }

            _timeBase = stream->time_base;
            _timeBaseSeconds = ffmpeg.av_q2d(_timeBase);
            _fps = ResolveFrameRate(stream);

            double durationSeconds = _format->duration > 0
                ? _format->duration / (double)ffmpeg.AV_TIME_BASE
                : stream->duration > 0
                    ? stream->duration * _timeBaseSeconds
                    : 0;

            int frameCount = stream->nb_frames > 0
                ? (int)stream->nb_frames
                : (int)Math.Round(durationSeconds * _fps);

            string pixelFormat = ffmpeg.av_get_pix_fmt_name((AVPixelFormat)codecParameters->format)
                ?? string.Empty;

            Info = new VideoInfo(
                codecParameters->width,
                codecParameters->height,
                _fps,
                frameCount,
                ffmpeg.avcodec_get_name(codecParameters->codec_id),
                TimeSpan.FromSeconds(durationSeconds))
            {
                PixelFormat = pixelFormat,
                BitDepth = ResolveBitDepth(codecParameters, pixelFormat),
                ColorSpace = ResolveColorSpace(codecParameters->color_space),
                ColorRange = ResolveColorRange(codecParameters->color_range),
                IsLossy = IsLossyCodec(codecParameters->codec_id),
            };
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>Absolute path of the file being decoded.</summary>
    public string Path { get; }

    public VideoInfo Info { get; }

    public static FrameDecoder Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string fullPath = System.IO.Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Video not found: {fullPath}", fullPath);
        }

        return new FrameDecoder(fullPath);
    }

    /// <summary>Decodes (or returns a cached) frame at the given zero-based index.</summary>
    public DecodedFrame DecodeFrame(int frameIndex)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (frameIndex < 0)
            {
                frameIndex = 0;
            }

            if (Info.HasKnownFrameCount && frameIndex >= Info.FrameCount)
            {
                frameIndex = Info.FrameCount - 1;
            }

            if (_cache.TryGetValue(frameIndex, out DecodedFrame? cached))
            {
                Touch(frameIndex);
                return cached;
            }

            if (_lastIndex < 0 || frameIndex < _lastIndex || frameIndex > _lastIndex + MaxForwardDecode)
            {
                Seek(frameIndex);
            }

            (int actualIndex, DecodedFrame frame) = DecodeForward(frameIndex);
            Store(actualIndex, frame);
            return frame;
        }
    }

    private static int ResolveBitDepth(AVCodecParameters* parameters, string pixelFormat)
    {
        if (parameters->bits_per_raw_sample > 0)
        {
            return parameters->bits_per_raw_sample;
        }

        if (pixelFormat.Contains("p016", StringComparison.Ordinal))
        {
            return 16;
        }

        if (pixelFormat.Contains("p012", StringComparison.Ordinal))
        {
            return 12;
        }

        if (pixelFormat.Contains("p010", StringComparison.Ordinal))
        {
            return 10;
        }

        foreach (int depth in (ReadOnlySpan<int>)[10, 12, 16, 9])
        {
            if (pixelFormat.Contains(depth.ToString(CultureInfo.InvariantCulture) + "le", StringComparison.Ordinal) ||
                pixelFormat.Contains(depth.ToString(CultureInfo.InvariantCulture) + "be", StringComparison.Ordinal))
            {
                return depth;
            }
        }

        return 8;
    }

    private static string ResolveColorSpace(AVColorSpace space) => space switch
    {
        AVColorSpace.AVCOL_SPC_BT709 => "bt709",
        AVColorSpace.AVCOL_SPC_BT470BG => "bt601",
        AVColorSpace.AVCOL_SPC_SMPTE170M => "smpte170m",
        AVColorSpace.AVCOL_SPC_BT2020_NCL => "bt2020",
        AVColorSpace.AVCOL_SPC_BT2020_CL => "bt2020",
        _ => string.Empty,
    };

    private static string ResolveColorRange(AVColorRange range) => range switch
    {
        AVColorRange.AVCOL_RANGE_JPEG => "pc",
        AVColorRange.AVCOL_RANGE_MPEG => "tv",
        _ => string.Empty,
    };

    private static bool IsLossyCodec(AVCodecID codec) => codec is not (
        AVCodecID.AV_CODEC_ID_RAWVIDEO or
        AVCodecID.AV_CODEC_ID_FFV1 or
        AVCodecID.AV_CODEC_ID_HUFFYUV or
        AVCodecID.AV_CODEC_ID_UTVIDEO or
        AVCodecID.AV_CODEC_ID_PNG or
        AVCodecID.AV_CODEC_ID_QTRLE);

    private static double ResolveFrameRate(AVStream* stream)
    {
        double fps = 0;

        if (stream->avg_frame_rate.den != 0 && stream->avg_frame_rate.num != 0)
        {
            fps = ffmpeg.av_q2d(stream->avg_frame_rate);
        }

        if (fps <= 0 && stream->r_frame_rate.den != 0 && stream->r_frame_rate.num != 0)
        {
            fps = ffmpeg.av_q2d(stream->r_frame_rate);
        }

        return fps > 0 ? fps : 25.0;
    }

    private void Seek(int frameIndex)
    {
        long target = (long)Math.Round(frameIndex / _fps / _timeBaseSeconds);

        // Seek to the closest keyframe at or before the target, then walk forward.
        int result = ffmpeg.avformat_seek_file(
            _format,
            _streamIndex,
            long.MinValue,
            target,
            long.MaxValue,
            AVSEEK_FLAG_BACKWARD);

        if (result < 0)
        {
            // Not fatal for many containers: fall back to a plain seek request.
            ffmpeg.avformat_seek_file(_format, _streamIndex, long.MinValue, target, long.MaxValue, 0);
        }

        ffmpeg.avcodec_flush_buffers(_codec);
        _lastIndex = -1;
        _draining = false;
    }

    private (int Index, DecodedFrame Frame) DecodeForward(int targetIndex)
    {
        while (true)
        {
            int result = ffmpeg.avcodec_receive_frame(_codec, _frame);

            if (result == 0)
            {
                int index = IndexFromPts(_frame->best_effort_timestamp);
                if (index <= _lastIndex)
                {
                    index = _lastIndex + 1;
                }

                _lastIndex = index;

                if (index >= targetIndex)
                {
                    DecodedFrame decoded = ConvertFrame(_frame);
                    ffmpeg.av_frame_unref(_frame);
                    return (index, decoded);
                }

                ffmpeg.av_frame_unref(_frame);
                continue;
            }

            if (result == AVERROR_EOF)
            {
                throw new EndOfStreamException($"Frame {targetIndex} is past the end of '{Path}'.");
            }

            if (result != ffmpeg.AVERROR(ffmpeg.EAGAIN))
            {
                throw new InvalidOperationException($"avcodec_receive_frame failed: {FfmpegRuntime.ErrorText(result)}");
            }

            if (!SendNextPacket())
            {
                throw new EndOfStreamException($"Frame {targetIndex} is past the end of '{Path}'.");
            }
        }
    }

    private bool SendNextPacket()
    {
        if (_draining)
        {
            return false;
        }

        while (true)
        {
            int result = ffmpeg.av_read_frame(_format, _packet);

            if (result < 0)
            {
                // End of file: flush the decoder and let receive_frame drain any buffered frames.
                _draining = true;
                ffmpeg.avcodec_send_packet(_codec, null);
                return true;
            }

            if (_packet->stream_index != _streamIndex)
            {
                ffmpeg.av_packet_unref(_packet);
                continue;
            }

            result = ffmpeg.avcodec_send_packet(_codec, _packet);
            ffmpeg.av_packet_unref(_packet);

            if (result == 0)
            {
                return true;
            }

            if (result == ffmpeg.AVERROR(ffmpeg.EAGAIN))
            {
                continue;
            }

            if (result == AVERROR_EOF)
            {
                _draining = true;
                return false;
            }

            throw new InvalidOperationException($"avcodec_send_packet failed: {FfmpegRuntime.ErrorText(result)}");
        }
    }

    private int IndexFromPts(long pts)
    {
        if (pts == long.MinValue || _fps <= 0 || _timeBaseSeconds <= 0)
        {
            return _lastIndex + 1;
        }

        return (int)Math.Round(pts * _timeBaseSeconds * _fps);
    }

    private DecodedFrame ConvertFrame(AVFrame* frame)
    {
        int width = frame->width;
        int height = frame->height;
        var format = (AVPixelFormat)frame->format;

        if (_sws == null || _swsWidth != width || _swsHeight != height || _swsFormat != (int)format)
        {
            if (_sws != null)
            {
                ffmpeg.sws_freeContext(_sws);
                _sws = null;
            }

            _sws = ffmpeg.sws_getContext(
                width, height, format,
                width, height, AVPixelFormat.AV_PIX_FMT_BGRA,
                (int)SwsFlags.SWS_BILINEAR, null, null, null);

            if (_sws == null)
            {
                throw new InvalidOperationException($"Could not create a BGRA scaler for {width}x{height} {format}.");
            }

            _swsWidth = width;
            _swsHeight = height;
            _swsFormat = (int)format;
        }

        int stride = width * 4;
        var pixels = new byte[stride * height];

        var sourceData = new byte*[4];
        var sourceStride = new int[4];
        for (int i = 0; i < 4; i++)
        {
            sourceData[i] = frame->data[(uint)i];
            sourceStride[i] = frame->linesize[(uint)i];
        }

        fixed (byte* destination = pixels)
        {
            var destinationData = new byte*[4] { destination, null, null, null };
            var destinationStride = new int[4] { stride, 0, 0, 0 };

            int rows = ffmpeg.sws_scale(_sws, sourceData, sourceStride, 0, height, destinationData, destinationStride);
            if (rows <= 0)
            {
                throw new InvalidOperationException("sws_scale did not convert the frame.");
            }
        }

        return new DecodedFrame(width, height, stride, pixels, frame->best_effort_timestamp);
    }

    private void Store(int index, DecodedFrame frame)
    {
        if (_cache.ContainsKey(index))
        {
            return;
        }

        _cache[index] = frame;
        _lru.AddLast(index);

        while (_lru.Count > CacheCapacity)
        {
            int evicted = _lru.First!.Value;
            _lru.RemoveFirst();
            _cache.Remove(evicted);
        }
    }

    private void Touch(int index)
    {
        LinkedListNode<int>? node = _lru.Find(index);
        if (node is null)
        {
            return;
        }

        _lru.Remove(node);
        _lru.AddLast(node);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (_sws != null)
            {
                ffmpeg.sws_freeContext(_sws);
                _sws = null;
            }

            if (_frame != null)
            {
                AVFrame* frame = _frame;
                ffmpeg.av_frame_free(&frame);
                _frame = null;
            }

            if (_packet != null)
            {
                AVPacket* packet = _packet;
                ffmpeg.av_packet_free(&packet);
                _packet = null;
            }

            if (_codec != null)
            {
                AVCodecContext* codec = _codec;
                ffmpeg.avcodec_free_context(&codec);
                _codec = null;
            }

            if (_format != null)
            {
                AVFormatContext* format = _format;
                ffmpeg.avformat_close_input(&format);
                _format = null;
            }

            _cache.Clear();
            _lru.Clear();
            _disposed = true;
        }
    }
}
