using VideoQuality.Core.Scoring;

namespace VideoQuality.App.ViewModels;

/// <summary>What <c>settings.json</c> remembers between runs.</summary>
public sealed class UserSettings
{
    public string Model { get; set; } = VmafModels.Default;

    public VmafBackend Backend { get; set; } = VmafBackend.Auto;

    /// <summary>Only an ffmpeg the user typed in Settings; the auto-resolved one is not saved, so a
    /// portable folder that moves keeps finding its bundled build.</summary>
    public string? FfmpegPath { get; set; }

    /// <summary>Folder holding mkvmerge/mkvextract; empty means find them automatically.</summary>
    public string? MkvToolNixFolder { get; set; }

    /// <summary>Folder holding the encoder ffmpeg; empty means the bundled copy, then PATH.</summary>
    public string? EncoderFolder { get; set; }

    /// <summary>How Mux and Pipeline tag tracks: <c>GreenTea</c> (default) or <c>Generic</c>.</summary>
    public string? TrackNaming { get; set; }

    /// <summary>Encodes run at once in the Encode queue.</summary>
    public int EncodeConcurrency { get; set; } = 1;

    /// <summary>UI language code (<c>en</c>, <c>zh-Hans</c>); empty follows the system.</summary>
    public string? Language { get; set; }

    public CompareMode CompareMode { get; set; } = CompareMode.SideBySide;

    public bool FixedScale { get; set; }

    public bool ShowSmoothed { get; set; }
}
