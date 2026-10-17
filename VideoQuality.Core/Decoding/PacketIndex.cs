using FFmpeg.AutoGen;

namespace VideoQuality.Core.Decoding;

/// <summary>One packet of the video stream, as demuxed (no decoding).</summary>
/// <param name="Pts">Presentation timestamp in stream time-base units, or long.MinValue.</param>
/// <param name="Dts">Decode timestamp in stream time-base units, or long.MinValue.</param>
/// <param name="Size">Packet size in bytes.</param>
/// <param name="Keyframe">True when the packet carries a keyframe.</param>
public sealed record VideoPacket(long Pts, long Dts, int Size, bool Keyframe);

/// <summary>
/// Packet-level view of a video stream. Walking packets is cheap (no decoding) and gives the true
/// video bitrate, the frame count when the container does not store one, and keyframe positions.
/// </summary>
public sealed unsafe class PacketIndex
{
    private PacketIndex(IReadOnlyList<VideoPacket> packets, long totalBytes, double durationSeconds)
    {
        Packets = packets;
        TotalBytes = totalBytes;
        DurationSeconds = durationSeconds;
    }

    public IReadOnlyList<VideoPacket> Packets { get; }

    /// <summary>Sum of the video stream's packet sizes; excludes container overhead and audio.</summary>
    public long TotalBytes { get; }

    /// <summary>Video stream duration in seconds (falls back to the container duration).</summary>
    public double DurationSeconds { get; }

    public int FrameCount => Packets.Count;

    /// <summary>True video bitrate in kbit/s.</summary>
    public double BitrateKbps => DurationSeconds > 0
        ? TotalBytes * 8.0 / DurationSeconds / 1000.0
        : 0;

    /// <summary>Indices (into <see cref="Packets"/>) that are keyframes.</summary>
    public IReadOnlyList<int> KeyframeIndices
    {
        get
        {
            var indices = new List<int>();
            for (int i = 0; i < Packets.Count; i++)
            {
                if (Packets[i].Keyframe)
                {
                    indices.Add(i);
                }
            }

            return indices;
        }
    }

    /// <summary>Builds an index from already-collected packets (used by tests and diagnostics).</summary>
    public static PacketIndex Create(IReadOnlyList<VideoPacket> packets, double durationSeconds)
    {
        long totalBytes = 0;
        foreach (VideoPacket packet in packets)
        {
            totalBytes += packet.Size;
        }

        return new PacketIndex(packets, totalBytes, durationSeconds);
    }

    public static PacketIndex Build(string path)
    {
        FfmpegRuntime.Initialize();

        AVFormatContext* format = null;
        AVPacket* packet = null;

        try
        {
            FrameDecoder.Check(ffmpeg.avformat_open_input(&format, path, null, null), "probe.error.open", path);
            FrameDecoder.Check(ffmpeg.avformat_find_stream_info(format, null), "probe.error.streamInfo", path);
            int streamIndex = ffmpeg.av_find_best_stream(format, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
            FrameDecoder.Ensure(streamIndex >= 0, "probe.error.noVideo", path);
            packet = ffmpeg.av_packet_alloc();
            FrameDecoder.Ensure(packet != null, "decoder.error.native", "av_packet_alloc");

            var packets = new List<VideoPacket>();
            long totalBytes = 0;

            while (ffmpeg.av_read_frame(format, packet) >= 0)
            {
                if (packet->stream_index == streamIndex)
                {
                    bool keyframe = (packet->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0;
                    packets.Add(new VideoPacket(packet->pts, packet->dts, packet->size, keyframe));
                    totalBytes += packet->size;
                }

                ffmpeg.av_packet_unref(packet);
            }

            // libavformat fills the container duration from the streams when it has none of its own.
            double duration = Math.Max(0, format->duration) / (double)ffmpeg.AV_TIME_BASE;

            return new PacketIndex(packets, totalBytes, duration);
        }
        finally
        {
            if (packet != null)
            {
                ffmpeg.av_packet_free(&packet);
            }

            if (format != null)
            {
                ffmpeg.avformat_close_input(&format);
            }
        }
    }
}
