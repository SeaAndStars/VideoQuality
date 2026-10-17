namespace VideoQuality.Core.Scoring;

/// <summary>
/// The VMAF models the tool knows about. One place so the app, CLI and tests cannot disagree.
/// </summary>
public static class VmafModels
{
    /// <summary>Original model, trained on 1080p SDR viewing conditions.</summary>
    public const string Standard = "vmaf_v0.6.1";

    /// <summary>Standard model with the enhancement gain removed. Better for encoder comparisons.</summary>
    public const string Neg = "vmaf_v0.6.1neg";

    /// <summary>Standard model with 4K viewing conditions.</summary>
    public const string Uhd = "vmaf_4k_v0.6.1";

    /// <summary>
    /// Default model. NEG is the better choice when comparing encoders: it does not reward
    /// sharpening or contrast changes that inflate plain VMAF.
    /// </summary>
    public const string Default = Neg;

    public static string DisplayName(string? model) => model switch
    {
        Neg => "VMAF NEG",
        Standard => "VMAF",
        Uhd => "VMAF 4K",
        null or "" => "VMAF",
        _ => model,
    };

    public static string ShortName(string? model) => model switch
    {
        Neg => "NEG",
        Standard => "STD",
        Uhd => "4K",
        null or "" => "VMAF",
        _ => model,
    };
}
