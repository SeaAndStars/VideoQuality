using VideoQuality.Core.Decoding;

namespace VideoQuality.Tests;

public class PacketIndexTests
{
    private static readonly VideoPacket[] Packets =
    [
        new(Pts: 0, Dts: 0, Size: 1000, Keyframe: true),
        new(Pts: 1, Dts: 1, Size: 2000, Keyframe: false),
        new(Pts: 2, Dts: 2, Size: 3000, Keyframe: true),
    ];

    [Fact]
    public void Bitrate_uses_video_bytes_only()
    {
        PacketIndex index = PacketIndex.Create(Packets, 2.0);

        Assert.Equal(6000, index.TotalBytes);
        Assert.Equal(24.0, index.BitrateKbps, 6); // 6000 bytes * 8 / 2 s / 1000
        Assert.Equal(3, index.FrameCount);
    }

    [Fact]
    public void Zero_duration_reports_no_bitrate()
    {
        PacketIndex index = PacketIndex.Create(Packets, 0);

        Assert.Equal(0, index.BitrateKbps);
    }

    [Fact]
    public void Keyframe_indices_are_reported()
    {
        PacketIndex index = PacketIndex.Create(Packets, 1.0);

        Assert.Equal(new[] { 0, 2 }, index.KeyframeIndices);
    }

    [Fact]
    public void Empty_index_is_safe()
    {
        PacketIndex index = PacketIndex.Create(Array.Empty<VideoPacket>(), 0);

        Assert.Equal(0, index.FrameCount);
        Assert.Equal(0, index.TotalBytes);
        Assert.Empty(index.KeyframeIndices);
    }
}
