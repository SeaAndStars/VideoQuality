using VideoQuality.Core.Scoring;

namespace VideoQuality.Core.Models;

/// <summary>
/// Everything a UI needs after one ffmpeg + libvmaf run.
/// </summary>
public sealed record VmafReport(
    IReadOnlyList<VmafFrame> Frames,
    VmafStats Stats,
    VmafExecutionBackend Backend,
    string CommandLine,
    TimeSpan Duration)
{
    public int FrameCount => Frames.Count;
}
