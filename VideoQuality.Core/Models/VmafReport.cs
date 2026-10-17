using VideoQuality.Core.Scoring;

namespace VideoQuality.Core.Models;

/// <summary>
/// Everything a UI or export needs after one ffmpeg + libvmaf run.
/// </summary>
public sealed record VmafReport(
    IReadOnlyList<VmafFrame> Frames,
    VmafStats Stats,
    VmafExecutionBackend Backend,
    string CommandLine,
    TimeSpan Duration)
{
    public int FrameCount => Frames.Count;

    /// <summary>Highest bit depth of the two inputs, as declared by the container/codec.</summary>
    public int SourceBitDepth { get; init; } = 8;

    /// <summary>
    /// True when the run happened on CUDA with a source deeper than 8 bits: libvmaf_cuda accepts
    /// yuv420p only, so the GPU scaler converted the frames to 8-bit before scoring.
    /// </summary>
    public bool ConvertedTo8Bit { get; init; }

    /// <summary>VMAF model used for the run.</summary>
    public string Model { get; init; } = string.Empty;

    /// <summary>True when Auto started on CUDA, failed, and was re-run on the CPU.</summary>
    public bool FellBackToCpu { get; init; }

    /// <summary>Why the CUDA attempt failed, when <see cref="FellBackToCpu"/> is set.</summary>
    public string? FallbackReason { get; init; }

    /// <summary>Long precision marker, e.g. <c>CUDA·10b→8b</c>.</summary>
    public string PrecisionLabel =>
        Backend.ToDisplayName() + (ConvertedTo8Bit ? $"·{SourceBitDepth}b→8b" : string.Empty);

    /// <summary>Compact marker for table columns, e.g. <c>CUDA·8b</c>.</summary>
    public string PrecisionShort =>
        Backend.ToDisplayName() + (ConvertedTo8Bit ? "·8b" : string.Empty);
}
