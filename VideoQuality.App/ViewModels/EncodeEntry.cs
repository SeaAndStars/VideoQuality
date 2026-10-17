using System.Globalization;
using System.IO;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using VideoQuality.Core.Models;
using VideoQuality.Core.Scoring;

namespace VideoQuality.App.ViewModels;

/// <summary>Where an encode is in the probe → packet index → score pipeline.</summary>
public enum EncodeState
{
    Queued,
    Indexing,
    WaitingForReference,
    Scoring,
    Done,
    Stale,
    Failed,
    Cancelled,
}

/// <summary>One distorted file being compared against the reference.</summary>
public partial class EncodeEntry : ObservableObject
{
    public EncodeEntry(string path, string colorHex)
    {
        Path = path;
        ColorHex = colorHex;
        Name = System.IO.Path.GetFileName(path);
        FileSize = File.Exists(path) ? new FileInfo(path).Length : 0;
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
    [NotifyPropertyChangedFor(nameof(ModelText))]
    [NotifyPropertyChangedFor(nameof(FramesText))]
    public partial VmafReport? Report { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BitrateText))]
    public partial double BitrateKbps { get; set; }

    /// <summary>Bits per component, filled in when the encode is first opened.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BadgeText))]
    public partial int BitDepth { get; set; }

    /// <summary>Probed stream geometry and rate, used to warn about mismatches with the reference.</summary>
    public int Width { get; set; }

    public int Height { get; set; }

    public double FrameRate { get; set; }

    /// <summary>Number of video packets, from the packet index; a cross-check on the decoded count.</summary>
    [ObservableProperty]
    public partial int PacketCount { get; set; }

    public string BadgeText => BitDepth > 8 ? $"{BitDepth}-bit" : string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(IsPending))]
    [NotifyPropertyChangedFor(nameof(IsWorking))]
    [NotifyPropertyChangedFor(nameof(IsDone))]
    [NotifyPropertyChangedFor(nameof(IsFailed))]
    [NotifyPropertyChangedFor(nameof(IsStale))]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    public partial EncodeState State { get; set; } = EncodeState.Queued;

    /// <summary>Score progress, 0..1, while <see cref="State"/> is Scoring.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial double Progress { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial string? Error { get; set; }

    /// <summary>Resolution, frame-rate or frame-count mismatch against the reference.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWarning))]
    public partial string? Warning { get; set; }

    /// <summary>The job currently working on this row, so it can be cancelled.</summary>
    public Core.Jobs.Job? ActiveJob { get; set; }

    public bool HasWarning => !string.IsNullOrEmpty(Warning);

    public bool IsPending => State is EncodeState.Queued or EncodeState.WaitingForReference;

    public bool IsWorking => State is EncodeState.Indexing or EncodeState.Scoring;

    public bool IsBusy => IsPending || IsWorking;

    public bool IsDone => State == EncodeState.Done;

    public bool IsFailed => State is EncodeState.Failed or EncodeState.Cancelled;

    public bool IsStale => State == EncodeState.Stale;

    /// <summary>Tooltip for the status icon.</summary>
    public string StatusText => State switch
    {
        EncodeState.Queued => "Queued",
        EncodeState.Indexing => "Reading packets…",
        EncodeState.WaitingForReference => "Waiting for a reference",
        EncodeState.Scoring => $"Scoring… {Progress * 100:0}%",
        EncodeState.Done => "Scored",
        EncodeState.Stale => "Stale: the reference changed. Re-score to update.",
        EncodeState.Cancelled => "Cancelled",
        _ => "Failed: " + (Error ?? "unknown error"),
    };

    /// <summary>Curve visibility; toggled by clicking the colour swatch.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SwatchOpacity))]
    public partial bool IsVisible { get; set; } = true;

    public double SwatchOpacity => IsVisible ? 1.0 : 0.25;

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

    public string ModelText => Report is null ? "—" : VmafModels.ShortName(Report.Model);

    public string FramesText => Report is null
        ? "—"
        : $"{Report.FrameCount}";
}
