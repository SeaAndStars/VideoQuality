namespace VideoQuality.Core.Models;

/// <summary>
/// One VMAF score, for one frame of the distorted video.
/// </summary>
/// <param name="FrameNum">Zero-based frame number as reported by libvmaf.</param>
/// <param name="Vmaf">VMAF score in the usual 0..100 range.</param>
public sealed record VmafFrame(int FrameNum, double Vmaf);
