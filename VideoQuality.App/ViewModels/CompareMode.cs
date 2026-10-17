namespace VideoQuality.App.ViewModels;

/// <summary>How the two inputs are presented.</summary>
public enum CompareMode
{
    /// <summary>Reference and encode side by side.</summary>
    SideBySide,

    /// <summary>One image, split by a draggable divider: reference left, encode right.</summary>
    Wipe,

    /// <summary>One image, toggling between reference and encode (hold F, or auto-flicker).</summary>
    Flicker,

    /// <summary>Reference next to an amplified difference heatmap.</summary>
    Difference,
}
