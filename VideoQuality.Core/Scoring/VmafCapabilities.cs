using VideoQuality.Core.Localization;
namespace VideoQuality.Core.Scoring;

/// <summary>
/// What the ffmpeg binary on this machine can actually do. Probed by running
/// <c>ffmpeg -filters</c> and checking for an NVIDIA device.
/// </summary>
public sealed record VmafCapabilities(
    bool HasLibvmaf,
    bool HasLibvmafCuda,
    bool HasNvidiaGpu,
    string FfmpegVersion)
{
    /// <summary>True when the CUDA VMAF filter can be used (filter present and a GPU visible).</summary>
    public bool CudaUsable => HasLibvmafCuda && HasNvidiaGpu;

    public static VmafCapabilities None { get; } = new(false, false, false, "not probed");

    public string Describe()
    {
        string libvmaf = HasLibvmaf ? "libvmaf" : Loc.T("capabilities.noLibvmaf");
        string cuda = HasLibvmafCuda ? "libvmaf_cuda" : Loc.T("capabilities.noLibvmafCuda");
        string gpu = Loc.T(HasNvidiaGpu ? "capabilities.gpu" : "capabilities.noGpu");
        return $"{libvmaf}, {cuda}, {gpu}";
    }
}
