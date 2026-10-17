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
/// decodes forward until the frame's timestamp matches. An LRU cache sized by a byte budget makes
/// stepping cheap: a backward step that has to re-seek also keeps the frames just before the
/// target, so the next left-arrow presses are cache hits instead of another keyframe walk.
/// </para>
/// <para>
/// One instance is not thread safe. The UI serialises calls through a semaphore.
/// </para>
/// </remarks>
public sealed unsafe class FrameDecoder : IDisposable
{
    private const long CacheBudgetBytes = 192L * 1024 * 1024;
    private const int MinCacheFrames = 4;
    private const int MaxCacheFrames = 64;
    private const int MaxForwardDecode = 240;
    private const int AVSEEK_FLAG_BACKWARD = 1;
    private const int AVERROR_EOF = -541478725;

    private readonly object _gate = new();
    private readonly Dictionary<int, LinkedListNode<(int Index, DecodedFrame Frame)>> _cache = new();
    private readonly LinkedList<(int Index, DecodedFrame Frame)> _lru = new();
    private readonly int _cacheCapacity;

    private AVFormatContext* _format;
    private AVCodecContext* _codec;
    private SwsContext* _sws;
    private AVFrame* _frame;
    private AVPacket* _packet;
    private int _lastIndex = -1;
    private int _keepFrom;
    private int _streamIndex;
    private AVRational _timeBase;
    private double _fps;
    private double _timeBaseSeconds;
    private bool _disposed;

    private FrameDecoder(string path)
    {
        Path = path;
        FfmpegRuntime.Initialize();

        AVFormatContext* format = null;
        try
        {
            Check(ffmpeg.avformat_open_input(&format, path, null, null), "probe.error.open", path);
            _format = format;
            Check(ffmpeg.avformat_find_stream_info(_format, null), "probe.error.streamInfo", path);

            _streamIndex = ffmpeg.av_find_best_stream(_format, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
            Ensure(_streamIndex >= 0, "decoder.error.noVideo", path);

            AVStream* stream = _format->streams[_streamIndex];
            AVCodecParameters* codecParameters = stream->codecpar;
            AVCodec* codec = ffmpeg.avcodec_find_decoder(codecParameters->codec_id);
            Ensure(codec != null, "decoder.error.noDecoder", ffmpeg.avcodec_get_name(codecParameters->codec_id));

            _codec = ffmpeg.avcodec_alloc_context3(codec);
            Ensure(_codec != null, "decoder.error.native", "avcodec_alloc_context3");
            Check(ffmpeg.avcodec_parameters_to_context(_codec, codecParameters), "decoder.error.call", "avcodec_parameters_to_context");
            _codec->pkt_timebase = stream->time_base;
            _codec->thread_count = 0; // let FFmpeg pick a thread count
            Check(ffmpeg.avcodec_open2(_codec, codec, null), "decoder.error.call", "avcodec_open2");

            _frame = ffmpeg.av_frame_alloc();
            _packet = ffmpeg.av_packet_alloc();
            Ensure(_frame != null && _packet != null, "decoder.error.native", "av_frame_alloc");

            _timeBase = stream->time_base;
            _timeBaseSeconds = ffmpeg.av_q2d(_timeBase);
            _fps = ResolveFrameRate(stream->avg_frame_rate, stream->r_frame_rate);

            // libavformat fills the container duration from the streams when it has none of its own.
            double durationSeconds = Math.Max(0, _format->duration) / (double)ffmpeg.AV_TIME_BASE;

            int frameCount = stream->nb_frames > 0
                ? (int)stream->nb_frames
                : (int)Math.Round(durationSeconds * _fps);

            // Interpolation turns an unknown format's null name into an empty string.
            string pixelFormat = $"{ffmpeg.av_get_pix_fmt_name((AVPixelFormat)codecParameters->format)}";

            Info = new VideoInfo(
                codecParameters->width,
                codecParameters->height,
                _fps,
                frameCount,
                ffmpeg.avcodec_get_name(codecParameters->codec_id),
                TimeSpan.FromSeconds(durationSeconds))
            {
                PixelFormat = pixelFormat,
                BitDepth = ResolveBitDepth(codecParameters->bits_per_raw_sample, pixelFormat),
                ColorSpace = ResolveColorSpace(codecParameters->color_space),
                ColorRange = ResolveColorRange(codecParameters->color_range),
                IsLossy = IsLossyCodec(codecParameters->codec_id),
            };

            _cacheCapacity = CacheCapacityFor(codecParameters->width, codecParameters->height);
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

    /// <summary>How many decoded frames the cache holds for this resolution.</summary>
    public int CacheCapacity => _cacheCapacity;

    public static FrameDecoder Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string fullPath = System.IO.Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException(Localization.Loc.T("decoder.error.missing", fullPath), fullPath);
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

            if (_cache.TryGetValue(frameIndex, out var cached))
            {
                _lru.Remove(cached);
                _lru.AddLast(cached);
                return cached.Value.Frame;
            }

            int keepFrom = frameIndex;
            if (_lastIndex >= 0 && frameIndex < _lastIndex && _lastIndex - frameIndex <= _cacheCapacity / 2)
            {
                // Stepping backwards: the keyframe walk decodes these frames anyway, so convert
                // the ones just before the target too. Half the cache keeps the frames the user
                // came from. A long backward jump (slider scrub) skips this; it rarely steps next.
                keepFrom = Math.Max(0, frameIndex - _cacheCapacity / 2);
            }

            // A fresh decoder is at the start: decode ahead unless the target is far. Seeking needs
            // an index, which raw elementary streams (.h264/.hevc) do not have.
            if (frameIndex < _lastIndex || frameIndex > _lastIndex + MaxForwardDecode)
            {
                Seek(frameIndex);
            }

            (int actualIndex, DecodedFrame frame) = DecodeForward(frameIndex, keepFrom);
            Store(actualIndex, frame);
            return frame;
        }
    }

    internal static int CacheCapacityFor(int width, int height)
    {
        long frameBytes = Math.Max(1L, (long)width * height * 4);
        return (int)Math.Clamp(CacheBudgetBytes / frameBytes, MinCacheFrames, MaxCacheFrames);
    }

    /// <summary>Throws <see cref="InvalidOperationException"/> with the text for <paramref name="key"/> unless <paramref name="ok"/>.</summary>
    internal static void Ensure(bool ok, string key, params object?[] args)
    {
        if (!ok)
        {
            throw new InvalidOperationException(Localization.Loc.T(key, args));
        }
    }

    /// <summary>Throws for a negative libav result, appending libav's description of the error.</summary>
    internal static void Check(int result, string key, string subject)
    {
        if (result < 0)
        {
            throw new InvalidOperationException(Localization.Loc.T(key, subject, FfmpegRuntime.ErrorText(result)));
        }
    }

    internal static int ResolveBitDepth(int bitsPerRawSample, string pixelFormat)
    {
        if (bitsPerRawSample > 0)
        {
            return bitsPerRawSample;
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

    internal static string ResolveColorSpace(AVColorSpace space) => space switch
    {
        AVColorSpace.AVCOL_SPC_BT709 => "bt709",
        AVColorSpace.AVCOL_SPC_BT470BG => "bt601",
        AVColorSpace.AVCOL_SPC_SMPTE170M => "smpte170m",
        AVColorSpace.AVCOL_SPC_BT2020_NCL => "bt2020",
        AVColorSpace.AVCOL_SPC_BT2020_CL => "bt2020",
        _ => string.Empty,
    };

    internal static string ResolveColorRange(AVColorRange range) => range switch
    {
        AVColorRange.AVCOL_RANGE_JPEG => "pc",
        AVColorRange.AVCOL_RANGE_MPEG => "tv",
        _ => string.Empty,
    };

    internal static bool IsLossyCodec(AVCodecID codec) => codec is not (
        AVCodecID.AV_CODEC_ID_RAWVIDEO or
        AVCodecID.AV_CODEC_ID_FFV1 or
        AVCodecID.AV_CODEC_ID_HUFFYUV or
        AVCodecID.AV_CODEC_ID_UTVIDEO or
        AVCodecID.AV_CODEC_ID_PNG or
        AVCodecID.AV_CODEC_ID_QTRLE);

    /// <summary>The average frame rate, else the real base rate, else 25.</summary>
    internal static double ResolveFrameRate(AVRational average, AVRational real)
    {
        double fps = average.den != 0 && average.num != 0 ? ffmpeg.av_q2d(average) : 0;
        if (fps <= 0 && real.den != 0 && real.num != 0)
        {
            fps = ffmpeg.av_q2d(real);
        }

        return fps > 0 ? fps : 25.0;
    }

    private void Seek(int frameIndex)
    {
        long target = (long)Math.Round(frameIndex / _fps / _timeBaseSeconds);

        // Seek to the closest keyframe at or before the target, then walk forward; some containers
        // refuse a backward seek, which is not fatal: fall back to a plain seek request.
        SeekWithFallback(flags => ffmpeg.avformat_seek_file(_format, _streamIndex, long.MinValue, target, long.MaxValue, flags));

        ffmpeg.avcodec_flush_buffers(_codec);
        _lastIndex = -1;
    }

    internal static void SeekWithFallback(Func<int, int> seek)
    {
        if (seek(AVSEEK_FLAG_BACKWARD) < 0)
        {
            seek(0);
        }
    }

    /// <summary>
    /// Decodes until the frame at or after <paramref name="targetIndex"/>. Frames from
    /// <paramref name="keepFrom"/> up to the target are converted and cached on the way.
    /// </summary>
    private (int Index, DecodedFrame Frame) DecodeForward(int targetIndex, int keepFrom)
    {
        _keepFrom = keepFrom;

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

                if (index >= keepFrom && !_cache.ContainsKey(index))
                {
                    Store(index, ConvertFrame(_frame));
                }

                ffmpeg.av_frame_unref(_frame);
                continue;
            }

            // EOF once the flushed decoder is empty; any other error is thrown; otherwise it wants input.
            if (result == AVERROR_EOF)
            {
                throw new EndOfStreamException(Localization.Loc.T("decoder.error.pastEnd", targetIndex, Path));
            }

            CheckReceive(result);
            SendNextPacket();
        }
    }

    /// <summary>avcodec_receive_frame's result when it returned no frame: EAGAIN is fine, anything else throws.</summary>
    internal static void CheckReceive(int result) =>
        Check(result == ffmpeg.AVERROR(ffmpeg.EAGAIN) ? 0 : result, "decoder.error.call", "avcodec_receive_frame");

    /// <summary>
    /// Feeds the next video packet; at the end of the file the flush packet, after which the decoder
    /// returns its buffered frames and then EOF.
    /// </summary>
    private void SendNextPacket()
    {
        while (true)
        {
            int result = ffmpeg.av_read_frame(_format, _packet);

            if (result < 0)
            {
                ffmpeg.avcodec_send_packet(_codec, null);
                return;
            }

            if (_packet->stream_index != _streamIndex)
            {
                ffmpeg.av_packet_unref(_packet);
                continue;
            }

            // Walking from a keyframe to the target: a frame nothing references and nobody will
            // see does not need decoding. The decoder applies skip_frame per packet, so frames
            // from keepFrom on are decoded normally.
            _codec->skip_frame = _packet->pts != ffmpeg.AV_NOPTS_VALUE && IndexFromPts(_packet->pts) < _keepFrom
                ? AVDiscard.AVDISCARD_NONREF
                : AVDiscard.AVDISCARD_DEFAULT;

            // The decoder's output is drained before every send, so it always accepts the packet.
            result = ffmpeg.avcodec_send_packet(_codec, _packet);
            ffmpeg.av_packet_unref(_packet);
            Check(result, "decoder.error.call", "avcodec_send_packet");
            return;
        }
    }

    /// <summary>Frame number of a timestamp; a frame without one follows the last.</summary>
    private int IndexFromPts(long pts) =>
        pts == long.MinValue ? _lastIndex + 1 : (int)Math.Round(pts * _timeBaseSeconds * _fps);

    private DecodedFrame ConvertFrame(AVFrame* frame)
    {
        int width = frame->width;
        int height = frame->height;
        var format = (AVPixelFormat)frame->format;

        // Reuses the scaler while size and format stay the same (they rarely change mid-stream).
        _sws = ffmpeg.sws_getCachedContext(
            _sws,
            width, height, format,
            width, height, AVPixelFormat.AV_PIX_FMT_BGRA,
            (int)SwsFlags.SWS_BILINEAR, null, null, null);
        Ensure(_sws != null, "decoder.error.scaler", width, height, format);

        int stride = width * 4;

        // sws_scale writes every byte, so skip zeroing ~8 MB per 1080p frame.
        byte[] pixels = GC.AllocateUninitializedArray<byte>(stride * height);

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
            Ensure(rows > 0, "decoder.error.native", "sws_scale");
        }

        return new DecodedFrame(width, height, stride, pixels, frame->best_effort_timestamp);
    }

    private void Store(int index, DecodedFrame frame)
    {
        if (_cache.ContainsKey(index))
        {
            return;
        }

        _cache[index] = _lru.AddLast((index, frame));

        while (_lru.Count > _cacheCapacity)
        {
            _cache.Remove(_lru.First!.Value.Index);
            _lru.RemoveFirst();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            ffmpeg.sws_freeContext(_sws); // accepts null
            _sws = null;

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
