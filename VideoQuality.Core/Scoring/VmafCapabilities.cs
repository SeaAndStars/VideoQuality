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
        string libvmaf = HasLibvmaf ? "libvmaf" : "no libvmaf";
        string cuda = HasLibvmafCuda ? "libvmaf_cuda" : "no libvmaf_cuda";
        string gpu = HasNvidiaGpu ? "NVIDIA GPU present" : "no NVIDIA GPU";
        return $"{libvmaf}, {cuda}, {gpu}";
    }
}
