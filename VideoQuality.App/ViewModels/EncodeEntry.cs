using System.Globalization;
using System.IO;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using VideoQuality.Core.Models;
using VideoQuality.Core.Scoring;

namespace VideoQuality.App.ViewModels;

/// <summary>One distorted file being compared against the reference.</summary>
public partial class EncodeEntry : ObservableObject
{
    public EncodeEntry(string path, string colorHex)
    {
        Path = path;
        ColorHex = colorHex;
        Name = System.IO.Path.GetFileName(path);
        FileSize = new FileInfo(path).Length;
        Swatch = new SolidColorBrush(Avalonia.Media.Color.Parse(colorHex));
        PlotColor = ScottPlot.Color.FromHex(colorHex);
    }

    public string Path { get; }

    public string Name { get; }

    public string ColorHex { get; }

    public long FileSize { get; }

    /// <summary>Colour used for the summary swatch.</summary>
    public IBrush Swatch { get; }

    /// <summary>Same colour for the ScottPlot curve.</summary>
    public ScottPlot.Color PlotColor { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReport))]
    [NotifyPropertyChangedFor(nameof(MeanText))]
    [NotifyPropertyChangedFor(nameof(P1Text))]
    [NotifyPropertyChangedFor(nameof(MaxText))]
    [NotifyPropertyChangedFor(nameof(BackendText))]
    [NotifyPropertyChangedFor(nameof(FramesText))]
    public partial VmafReport? Report { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BitrateText))]
    public partial double BitrateKbps { get; set; }

    /// <summary>Bits per component, filled in when the encode is first opened.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BadgeText))]
    public partial int BitDepth { get; set; }

    /// <summary>Number of video packets, from the packet index; a cross-check on the decoded count.</summary>
    [ObservableProperty]
    public partial int PacketCount { get; set; }

    public string BadgeText => BitDepth > 8 ? $"{BitDepth}-bit" : string.Empty;

    public bool HasReport => Report is not null;

    public string MeanText => Report is null
        ? "—"
        : Report.Stats.Mean.ToString("F2", CultureInfo.InvariantCulture);

    public string P1Text => Report is null
        ? "—"
        : Report.Stats.P1.ToString("F2", CultureInfo.InvariantCulture);

    public string MaxText => Report is null
        ? "—"
        : Report.Stats.Max.ToString("F2", CultureInfo.InvariantCulture);

    public string BitrateText => BitrateKbps <= 0
        ? "—"
        : $"{BitrateKbps.ToString("F0", CultureInfo.InvariantCulture)} kb/s";

    public string SizeText => $"{FileSize / 1024.0 / 1024.0:F1} MB";

    public string BackendText => Report?.PrecisionShort ?? "—";

    public string FramesText => Report is null
        ? "—"
        : $"{Report.FrameCount}";
}
