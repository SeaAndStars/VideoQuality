namespace VideoQuality.Core.Scoring;

/// <summary>Which VMAF implementation the caller wants.</summary>
public enum VmafBackend
{
    /// <summary>Use CUDA when the ffmpeg build and the machine support it, otherwise CPU.</summary>
    Auto = 0,

    /// <summary>Force the CPU libvmaf filter.</summary>
    Cpu = 1,

    /// <summary>Force libvmaf_cuda (requires a CUDA-enabled ffmpeg and an NVIDIA GPU).</summary>
    Cuda = 2,
}

/// <summary>Which VMAF implementation actually ran.</summary>
public enum VmafExecutionBackend
{
    Cpu = 0,
    Cuda = 1,
}

public static class VmafExecutionBackendExtensions
{
    /// <summary>Display name; the enum member is <c>Cuda</c>, but the UI should say "CUDA".</summary>
    public static string ToDisplayName(this VmafExecutionBackend backend) =>
        backend == VmafExecutionBackend.Cuda ? "CUDA" : "CPU";
}
