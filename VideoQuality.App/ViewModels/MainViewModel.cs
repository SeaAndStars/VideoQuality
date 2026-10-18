using VideoQuality.App.Services;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using VideoQuality.Core.Diagnostics;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VideoQuality.Core.Decoding;
using VideoQuality.Core.Imaging;
using VideoQuality.Core.Ingest;
using VideoQuality.Core.Jobs;
using VideoQuality.Core.Models;
using VideoQuality.Core.Scoring;
using VideoQuality.Core.Settings;
using VideoQuality.Core.Localization;

namespace VideoQuality.App.ViewModels;

/// <summary>
/// One <see cref="CurrentFrame"/> drives both decoders, the slider, the keyboard, playback and the
/// plot. Encodes are scored against one reference and overlaid on the graph; the selected encode is
/// what the viewer compares against.
/// </summary>
public partial class MainViewModel : ViewModelBase, IDisposable
{
    // Okabe–Ito: distinguishable with the common colour-vision deficiencies, ordered for a dark
    // background (black swapped for a light grey).
    private static readonly string[] Palette =
    [
        "#56B4E9", "#E69F00", "#009E73", "#D55E00",
        "#CC79A7", "#F0E442", "#0072B2", "#BBBBBB",
    ];

    // Two CUDA scores overlap well (each is NVDEC-bound); CPU libvmaf already uses every core, so
    // CPU runs take this gate as well and stay one at a time.
    private readonly JobService _jobs = new(scoreConcurrency: 2);
    private readonly SemaphoreSlim _cpuScoreGate = new(1, 1);

    private readonly SemaphoreSlim _decodeLock = new(1, 1);
    private FrameDecoder? _referenceDecoder;
    private FrameDecoder? _distortedDecoder;
    private int _loadVersion;
    private DispatcherTimer? _playTimer;
    private readonly Stopwatch _playClock = new();
    private int _playStartFrame;
    private DispatcherTimer? _flickerTimer;
    // Exact reference frame count from a packet walk; 0 until known. Matroska stores no frame count,
    // so the decoder's duration × fps estimate must not be compared with an encode's exact count.
    private int _referencePacketCount;
    private readonly BitmapSwapChain _referenceBitmaps = new();
    private readonly BitmapSwapChain _distortedBitmaps = new();
    private readonly BitmapSwapChain _differenceBitmaps = new();
    private DecodedFrame? _lastReferenceFrame;
    private DecodedFrame? _lastDistortedFrame;
    private VmafCapabilities? _capabilities;

    public MainViewModel(AppServices? services = null)
    {
        Services = services ?? AppServices.Default;
        Viewport = new ViewportState();
        Encodes.CollectionChanged += OnEncodesChanged;

        LoadSettings();

        string? configuredFfmpeg = Services.Environment("VQ_FFMPEG");
        if (!string.IsNullOrWhiteSpace(configuredFfmpeg))
        {
            _settingsQuiet = true;
            FfmpegPath = configuredFfmpeg;
            _settingsQuiet = false;
        }
    }

    /// <summary>External processes (ffmpeg probes and scoring); tests pass scripted ones.</summary>
    public AppServices Services { get; }

    // ---------------------------------------------------------------- settings.json

    private UserSettings _settings = new();
    private bool _settingsQuiet;
    private DispatcherTimer? _saveTimer;

    private void LoadSettings()
    {
        _settings = Services.Settings.Load();
        _settingsQuiet = true;
        try
        {
            SelectedModel = string.IsNullOrWhiteSpace(_settings.Model) ? VmafModels.Default : _settings.Model;
            SelectedBackend = _settings.Backend;
            CompareMode = _settings.CompareMode;
            FixedScale = _settings.FixedScale;
            ShowSmoothed = _settings.ShowSmoothed;

            // A saved ffmpeg that has since moved or been deleted would only break scoring.
            if (_settings.FfmpegPath is { Length: > 0 } saved && File.Exists(saved))
            {
                FfmpegPath = saved;
            }
        }
        finally
        {
            _settingsQuiet = false;
        }
    }

    /// <summary>Applies a startup --mode without remembering it as the user's preference.</summary>
    public void ApplyStartupMode(CompareMode mode)
    {
        _settingsQuiet = true;
        CompareMode = mode;
        _settingsQuiet = false;
    }

    /// <summary>Saves half a second after the last change, so dragging a control writes once.</summary>
    /// <summary>The persisted settings, shared with the other pages (read-only view).</summary>
    public UserSettings PersistedSettings => _settings;

    /// <summary>Lets other pages change persisted settings through the same debounced save.</summary>
    public void UpdateSettings(Action<UserSettings> change) => ScheduleSave(change);

    private void ScheduleSave(Action<UserSettings> change)
    {
        if (_settingsQuiet)
        {
            return;
        }

        change(_settings);
        _saveTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) =>
        {
            _saveTimer!.Stop();
            Services.Settings.Save(_settings);
        });
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>Writes pending changes immediately (window closing).</summary>
    public void FlushSettings()
    {
        if (_saveTimer is { IsEnabled: true })
        {
            _saveTimer.Stop();
            Services.Settings.Save(_settings);
        }
    }

    public ViewportState Viewport { get; }

    /// <summary>Raised for transient feedback (duplicates, rejected files, Undo). The view shows a toast.</summary>
    public event EventHandler<ToastRequest>? ToastRequested;

    /// <summary>
    /// Asks the user a yes/no question (title, message) and returns true for yes. Set by the view;
    /// without it, questions resolve to their default (yes).
    /// </summary>
    public Func<string, string, Task<bool>>? ConfirmAsync { get; set; }

    public ObservableCollection<EncodeEntry> Encodes { get; } = [];

    public CompareMode[] CompareModes { get; } =
        [CompareMode.SideBySide, CompareMode.Wipe, CompareMode.Flicker, CompareMode.Difference];

    public DifferenceChannels[] DifferenceChannelOptions { get; } =
        [DifferenceChannels.Luma, DifferenceChannels.Chroma, DifferenceChannels.Both];

    public VmafBackend[] BackendOptions { get; } = [VmafBackend.Auto, VmafBackend.Cpu, VmafBackend.Cuda];

    public string[] ModelOptions { get; } = [VmafModels.Neg, VmafModels.Standard, VmafModels.Uhd];

    [ObservableProperty]
    public partial string? ReferencePath { get; set; }

    [ObservableProperty]
    public partial EncodeEntry? SelectedEncode { get; set; }

    [ObservableProperty]
    public partial CompareMode CompareMode { get; set; } = CompareMode.SideBySide;

    [ObservableProperty]
    public partial DifferenceChannels DifferenceChannels { get; set; } = DifferenceChannels.Both;

    [ObservableProperty]
    public partial double DifferenceGain { get; set; } = 4.0;

    [ObservableProperty]
    public partial double WipeSplit { get; set; } = 0.5;

    [ObservableProperty]
    public partial bool FlickerShowsDistorted { get; set; }

    [ObservableProperty]
    public partial bool AutoFlicker { get; set; }

    [ObservableProperty]
    public partial bool FixedScale { get; set; }

    /// <summary>Draw a 5-frame moving average instead of the raw per-frame curve.</summary>
    [ObservableProperty]
    public partial bool ShowSmoothed { get; set; }

    /// <summary>Plot VMAF relative to the first visible encode instead of absolute values.</summary>
    [ObservableProperty]
    public partial bool ShowDelta { get; set; }

    [ObservableProperty]
    public partial bool IsPlaying { get; set; }

    [ObservableProperty]
    public partial string PlayPauseHint { get; set; } = Loc.T("compare.play");

    [ObservableProperty]
    public partial int CurrentFrame { get; set; }

    [ObservableProperty]
    public partial double CurrentScore { get; set; }

    [ObservableProperty]
    public partial double FrameRate { get; set; }

    [ObservableProperty]
    public partial double ImageWidth { get; set; }

    [ObservableProperty]
    public partial double ImageHeight { get; set; }

    [ObservableProperty]
    public partial string FfmpegPath { get; set; } = "ffmpeg";

    [ObservableProperty]
    public partial VmafBackend SelectedBackend { get; set; } = VmafBackend.Auto;

    [ObservableProperty]
    public partial string SelectedModel { get; set; } = VmafModels.Default;

    [ObservableProperty]
    public partial string Status { get; set; } = Loc.T("compare.status.start");

    [ObservableProperty]
    public partial string BackendStatus { get; set; } = Loc.T("compare.backend.probing");

    [ObservableProperty]
    public partial string ReferenceInfo { get; set; } = Loc.T("compare.noReference");

    [ObservableProperty]
    public partial string DistortedInfo { get; set; } = Loc.T("compare.noEncode");

    [ObservableProperty]
    public partial string ReferenceName { get; set; } = Loc.T("compare.referenceName");

    [ObservableProperty]
    public partial string EncodeName { get; set; } = Loc.T("compare.encodeName");

    [ObservableProperty]
    public partial string ReferenceBadge { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string EncodeBadge { get; set; } = string.Empty;

    [ObservableProperty]
    public partial double ProgressPercent { get; set; }

    [ObservableProperty]
    public partial bool ProgressIndeterminate { get; set; }

    [ObservableProperty]
    public partial string ProgressDetail { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string LogPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial WriteableBitmap? ReferenceBitmap { get; set; }

    [ObservableProperty]
    public partial WriteableBitmap? DistortedBitmap { get; set; }

    [ObservableProperty]
    public partial WriteableBitmap? DifferenceBitmap { get; set; }

    public VmafReport? Report => SelectedEncode?.Report;

    public IReadOnlyList<VmafFrame> Frames => SelectedEncode?.Report?.Frames ?? Array.Empty<VmafFrame>();

    public int FrameCount => Frames.Count;

    // At least 1 so an empty (disabled) slider shows its thumb at the start, not the far end.
    public double MaxFrame => Math.Max(1, FrameCount - 1);

    public bool HasFrames => FrameCount > 1;

    public bool HasEncodes => Encodes.Count > 0;

    public bool HasReference => !string.IsNullOrWhiteSpace(ReferencePath);

    /// <summary>Nothing loaded yet: the viewer shows the two drop zones as the first-run screen.</summary>
    public bool IsEmpty => !HasReference && Encodes.Count == 0;

    /// <summary>A drag with files is over the window; the view shows the drop overlay.</summary>
    [ObservableProperty]
    public partial bool IsDragging { get; set; }

    public bool HasStaleEncodes => Encodes.Any(e => e.IsStale);

    /// <summary>Short backend label for the toolbar chip, e.g. "CUDA" or "CPU".</summary>
    public string BackendChip => SelectedBackend switch
    {
        VmafBackend.Cpu => "CPU",
        VmafBackend.Cuda => "CUDA",
        _ => _capabilities is { CudaUsable: true } ? "CUDA" : _capabilities is null ? "…" : "CPU",
    };

    public string ModelChip => VmafModels.ShortName(SelectedModel);

    /// <summary>Centre of the title bar: what is being compared.</summary>
    public string ComparisonTitle => HasReference
        ? Loc.T(Encodes.Count == 1 ? "compare.title.one" : "compare.title.many", ReferenceName, Encodes.Count)
        : string.Empty;

    /// <summary>Taskbar / Alt-Tab title.</summary>
    public string WindowTitle => HasReference ? $"{ReferenceName} — VideoQuality" : "VideoQuality";

    public bool HasReferenceBadge => ReferenceBadge.Length > 0;

    public bool HasEncodeBadge => EncodeBadge.Length > 0;

    public bool CanPlay => FrameCount > 1 && FrameRate > 0;

    public bool IsTwoPane => CompareMode is CompareMode.SideBySide or CompareMode.Difference;

    public bool IsSinglePane => CompareMode is CompareMode.Wipe or CompareMode.Flicker;

    public bool IsWipe => CompareMode == CompareMode.Wipe;

    public bool IsFlicker => CompareMode == CompareMode.Flicker;

    public bool IsDifference => CompareMode == CompareMode.Difference;

    public bool IsAutoFlicker => IsFlicker && AutoFlicker;

    public WriteableBitmap? SingleBitmap => FlickerShowsDistorted ? DistortedBitmap : ReferenceBitmap;

    public string RightPaneTitle => IsDifference
        ? Loc.T("compare.pane.difference", DifferenceGain.ToString("0.#", CultureInfo.InvariantCulture), ChannelLabel)
        : Loc.T("compare.pane.encode");

    public string DifferenceLegend
    {
        get
        {
            int top = (int)Math.Round(255.0 / Math.Max(1.0, DifferenceGain));
            string channel = DifferenceChannels switch
            {
                DifferenceChannels.Luma => "ΔY",
                DifferenceChannels.Chroma => "ΔC",
                _ => "ΔY/C",
            };
            return Loc.T("compare.difference.legend", channel, top, DifferenceGain.ToString("0.#", CultureInfo.InvariantCulture));
        }
    }

    public string ChannelLabel => DifferenceChannels switch
    {
        DifferenceChannels.Luma => Loc.T("compare.channel.luma"),
        DifferenceChannels.Chroma => Loc.T("compare.channel.chroma"),
        _ => Loc.T("compare.channel.both"),
    };

    public string SinglePaneTitle => IsWipe
        ? Loc.T("compare.pane.wipe")
        : Loc.T("compare.pane.flicker");

    public double CurrentFrameValue
    {
        get => CurrentFrame;
        set
        {
            int index = (int)Math.Round(value);
            if (index != CurrentFrame)
            {
                CurrentFrame = index;
            }
        }
    }

    public string FrameText
    {
        get
        {
            string label = VmafModels.DisplayName(Report?.Model ?? SelectedModel);
            return Frames.Count == 0
                ? Loc.T("compare.frameText.empty", label)
                : Loc.T(
                    "compare.frameText",
                    CurrentFrame + 1,
                    Frames.Count,
                    label,
                    CurrentScore);
        }
    }

    public bool HasMixedModels { get; private set; }

    public string StatsText
    {
        get
        {
            if (Report is not { } report)
            {
                return string.Empty;
            }

            VmafStats stats = report.Stats;
            return Loc.T(
                "compare.statsText",
                stats.Mean,
                stats.HarmonicMean,
                stats.P1,
                stats.Min,
                stats.Max,
                stats.StdDev,
                report.Backend.ToDisplayName(),
                VmafModels.ShortName(report.Model));
        }
    }

    public async Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(FfmpegPath) || string.Equals(FfmpegPath, "ffmpeg", StringComparison.Ordinal))
        {
            string resolved = await VmafRunner.ResolveFfmpegAsync(FfmpegPath, Services.Processes).ConfigureAwait(true);
            if (!string.Equals(resolved, FfmpegPath, StringComparison.Ordinal))
            {
                _settingsQuiet = true; // auto-detected, not the user's choice
                FfmpegPath = resolved;
                _settingsQuiet = false;
            }
        }

        await ProbeBackendAsync().ConfigureAwait(true);
        LogSession();
    }

    private void LogSession()
    {
        VqLog.SessionHeader("app session", new Dictionary<string, string>
        {
            ["core"] = typeof(VmafRunner).Assembly.GetName().Version!.ToString(),
            ["dotnet"] = Environment.Version.ToString(),
            ["os"] = RuntimeInformation.OSDescription,
            ["ffmpeg"] = FfmpegPath,
            ["backend"] = _capabilities!.Describe(), // probed just before
            ["libav"] = SafeLibav(),
            ["log"] = VqLog.CurrentPath ?? "(disabled)",
        });
    }

    private string SafeLibav()
    {
        try
        {
            return Services.DescribeLibav();
        }
        catch (Exception ex)
        {
            return Loc.T("capabilities.unavailable", ex.Message);
        }
    }

    public async Task SetReferenceAsync(string path)
    {
        ReferencePath = path;

        await _decodeLock.WaitAsync().ConfigureAwait(true);
        try
        {
            _referenceDecoder?.Dispose();
            _referenceDecoder = null;

            string captured = path;
            _referenceDecoder = await Task.Run(() => FrameDecoder.Open(captured)).ConfigureAwait(true);

            VideoInfo info = _referenceDecoder.Info;
            FrameRate = info.FrameRate;
            ImageWidth = info.Width;
            ImageHeight = info.Height;
            ReferenceInfo = Describe(Path.GetFileName(path), info);
            ReferenceName = Path.GetFileName(path);
            ReferenceBadge = info.Badge;
            OnPropertyChanged(nameof(HasReferenceBadge));

            Status = Encodes.Count == 0
                ? Loc.T("compare.status.referenceLoadedAdd")
                : Loc.T("compare.status.referenceLoaded");
        }
        catch (Exception ex)
        {
            ReferenceInfo = Loc.T("compare.noReference");
            Status = Loc.T("compare.status.referenceFailed", ex.Message);
        }
        finally
        {
            _decodeLock.Release();
        }

        await LoadFrameAsync().ConfigureAwait(true);

        if (_referenceDecoder is not null)
        {
            _ = CountReferenceFramesAsync(path);
        }
    }

    /// <summary>Exact reference frame count, then refresh the mismatch warnings that depend on it.</summary>
    private async Task CountReferenceFramesAsync(string path)
    {
        _referencePacketCount = 0;
        int count = 0;

        Job job = _jobs.Enqueue(new Job
        {
            Title = Loc.T("compare.job.indexReference", Path.GetFileName(path)),
            Kind = JobKind.Index,
            Work = (_, _, token) => Task.Run(() => count = PacketIndex.Build(path).FrameCount, token),
        });

        await job.Completion.ConfigureAwait(true);
        if (job.State == JobState.Succeeded && string.Equals(path, ReferencePath, StringComparison.Ordinal))
        {
            _referencePacketCount = count;
            UpdateWarnings();
        }
    }

    /// <summary>
    /// The single entry point for incoming files: drop zones, file pickers, startup arguments,
    /// Open With and paste all route through <see cref="DropRouter"/> and land here.
    /// </summary>
    public async Task HandleDropAsync(IReadOnlyList<string?> paths, DropZone zone)
    {
        IsDragging = false;
        if (paths.Count == 0)
        {
            return;
        }

        DropPlan plan = DropRouter.Plan(paths, zone, ReferencePath, Encodes.Select(e => e.Path).ToList());

        if (plan.Rejected.Count > 0)
        {
            string lines = string.Join(Environment.NewLine, plan.Rejected
                .Take(4)
                .Select(r => $"{(r.Path.Length > 0 ? Path.GetFileName(r.Path) : Loc.T("compare.drop.item"))} — {r.Reason}"));
            string more = plan.Rejected.Count > 4 ? Environment.NewLine + Loc.T("compare.drop.more", plan.Rejected.Count - 4) : string.Empty;
            Toast(ToastKind.Warning, Loc.T("compare.drop.skipped", plan.Rejected.Count) + Environment.NewLine + lines + more);
        }

        if (plan.Duplicates.Count > 0)
        {
            Toast(ToastKind.Info, plan.Duplicates.Count == 1
                ? Loc.T("compare.drop.duplicate", Path.GetFileName(plan.Duplicates[0]))
                : Loc.T("compare.drop.duplicates", plan.Duplicates.Count));
        }

        if (plan.IsEmpty)
        {
            return;
        }

        string? previousReference = ReferencePath;
        bool rescore = true;

        if (plan.Reference is not null)
        {
            if (plan.NeedsConfirm)
            {
                int affected = Encodes.Count(e => !string.Equals(e.Path, plan.Reference, StringComparison.Ordinal));
                rescore = ConfirmAsync is null || await ConfirmAsync(
                    Loc.T("compare.confirmRescore.title", affected),
                    Loc.T("compare.confirmRescore.message", Path.GetFileName(plan.Reference))).ConfigureAwait(true);
            }

            // A file that was an encode is now the reference; it leaves the table.
            if (Encodes.FirstOrDefault(e => string.Equals(e.Path, plan.Reference, StringComparison.Ordinal)) is { } promoted)
            {
                RemoveEncode(promoted);
            }

            await SetReferenceAsync(plan.Reference).ConfigureAwait(true);
            if (_referenceDecoder is null)
            {
                return;
            }

            foreach (EncodeEntry entry in Encodes)
            {
                CancelJob(entry);
                entry.Report = null;
                entry.State = EncodeState.Stale;
                entry.Warning = null;
            }

            NotifyReportChanged();
        }

        List<EncodeEntry> added = AddRows(plan.Encodes);

        // Rows that were waiting for a reference, and (when confirmed) stale rows, score now.
        if (HasReference)
        {
            foreach (EncodeEntry entry in Encodes.Where(e => !added.Contains(e)).ToList())
            {
                if (entry.State == EncodeState.WaitingForReference || (rescore && entry.State == EncodeState.Stale))
                {
                    _ = ProcessEncodeAsync(entry, indexFirst: entry.PacketCount <= 0);
                }
            }

            UpdateWarnings();
        }

        foreach (EncodeEntry entry in added)
        {
            _ = ProcessEncodeAsync(entry, indexFirst: true);
        }

        if (plan.ReferenceOverflowed)
        {
            Toast(ToastKind.Info,
                Loc.T("compare.drop.referenceOverflow", Path.GetFileName(plan.Reference), added.Count),
                Loc.T("common.undo"),
                () => _ = UndoDropAsync(previousReference, added));
        }
        else if (!HasReference && added.Count > 0)
        {
            Status = Loc.T("compare.status.encodesQueued");
        }

        OnPropertyChanged(nameof(HasStaleEncodes));
    }

    private List<EncodeEntry> AddRows(IReadOnlyList<string> paths)
    {
        var added = new List<EncodeEntry>();
        foreach (string path in paths)
        {
            var entry = new EncodeEntry(path, NextColor())
            {
                State = HasReference ? EncodeState.Queued : EncodeState.WaitingForReference,
            };

            Encodes.Add(entry);
            added.Add(entry);
        }

        if (SelectedEncode is null && added.Count > 0)
        {
            SelectedEncode = added[0];
        }

        return added;
    }

    private string NextColor()
    {
        var used = Encodes.Select(e => e.ColorHex).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Palette.FirstOrDefault(c => !used.Contains(c)) ?? Palette[Encodes.Count % Palette.Length];
    }

    private async Task UndoDropAsync(string? previousReference, IReadOnlyList<EncodeEntry> added)
    {
        foreach (EncodeEntry entry in added)
        {
            RemoveEncode(entry);
        }

        if (previousReference is null)
        {
            ClearReference();
        }
        else
        {
            await SetReferenceAsync(previousReference).ConfigureAwait(true);
        }

        Status = Loc.T("compare.status.dropUndone");
    }

    private void ClearReference()
    {
        _referenceDecoder?.Dispose();
        _referenceDecoder = null;
        _referencePacketCount = 0;
        ReferencePath = null;
        ReferenceInfo = Loc.T("compare.noReference");
        ReferenceName = Loc.T("compare.referenceName");
        ReferenceBadge = string.Empty;
        ReferenceBitmap = null;
        _lastReferenceFrame = null;
        DifferenceBitmap = null;

        foreach (EncodeEntry entry in Encodes)
        {
            CancelJob(entry);
            entry.Report = null;
            entry.State = EncodeState.WaitingForReference;
        }

        NotifyReportChanged();
    }

    /// <summary>
    /// Runs one row through the pipeline: probe + packet index (index lane), then a score (score
    /// lane). Rows update in place, so the table shows each stage as it happens.
    /// </summary>
    private async Task ProcessEncodeAsync(EncodeEntry entry, bool indexFirst)
    {
        if (indexFirst)
        {
            entry.State = EncodeState.Indexing;
            entry.Error = null;
            UpdateJobSummary();

            MediaProbeResult? probe = null;
            PacketIndex? index = null;
            string path = entry.Path;

            Job indexJob = _jobs.Enqueue(new Job
            {
                Title = Loc.T("compare.job.index", entry.Name),
                Kind = JobKind.Index,
                Subject = entry,
                Work = (_, _, token) => Task.Run(() =>
                {
                    probe = MediaProbe.Probe(path);
                    token.ThrowIfCancellationRequested();
                    index = PacketIndex.Build(path);
                }, token),
            });

            entry.ActiveJob = indexJob;
            await indexJob.Completion.ConfigureAwait(true);
            if (!ReferenceEquals(entry.ActiveJob, indexJob) || !Encodes.Contains(entry))
            {
                return; // removed or superseded while indexing
            }

            entry.ActiveJob = null;

            if (indexJob.State != JobState.Succeeded)
            {
                entry.State = indexJob.State == JobState.Cancelled ? EncodeState.Cancelled : EncodeState.Failed;
                entry.Error = indexJob.Error ?? Loc.T("compare.error.unreadable");
                UpdateJobSummary();
                return;
            }

            // A finished index job filled both in.
            entry.BitDepth = probe!.BitDepth;
            entry.Width = probe.Width;
            entry.Height = probe.Height;
            entry.FrameRate = probe.FrameRate;
            entry.PacketCount = index!.FrameCount;
            entry.BitrateKbps = index.BitrateKbps > 0
                ? index.BitrateKbps
                : _referenceDecoder?.Info.Duration.TotalSeconds is double seconds and > 0
                    ? entry.FileSize * 8.0 / seconds / 1000.0
                    : 0;
        }

        if (!HasReference || _referenceDecoder is null)
        {
            entry.State = EncodeState.WaitingForReference;
            UpdateJobSummary();
            return;
        }

        entry.Warning = DescribeMismatch(_referenceDecoder.Info, entry);
        await ScoreEntryAsync(entry).ConfigureAwait(true);
    }

    private async Task ScoreEntryAsync(EncodeEntry entry)
    {
        CancelJob(entry);
        entry.State = EncodeState.Queued;
        entry.Progress = 0;
        entry.Error = null;
        UpdateJobSummary();

        string reference = ReferencePath!;
        // Scoring starts only with the reference open (ProcessEncodeAsync checks).
        int totalFrames = _referencePacketCount > 0 ? _referencePacketCount : _referenceDecoder!.Info.FrameCount;
        VmafBackend backend = SelectedBackend;
        string model = SelectedModel;
        bool cpu = backend == VmafBackend.Cpu || _capabilities is { CudaUsable: false };
        VmafReport? report = null;

        // Created here, on the UI thread, so reports are marshalled back to it.
        var progress = new Progress<VmafProgress>(p =>
        {
            if (p.FrameNumber is int frame && totalFrames > 0 && entry.State == EncodeState.Scoring)
            {
                entry.Progress = Math.Min(1, frame / (double)totalFrames);
                UpdateJobSummary();
            }
        });

        Job job = _jobs.Enqueue(new Job
        {
            Title = Loc.T("compare.job.score", entry.Name),
            Kind = JobKind.Score,
            Subject = entry,
            Work = async (_, _, token) =>
            {
                if (cpu)
                {
                    await _cpuScoreGate.WaitAsync(token).ConfigureAwait(false);
                }

                try
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (ReferenceEquals(entry.ActiveJob?.Subject, entry) && entry.State == EncodeState.Queued)
                        {
                            entry.State = EncodeState.Scoring;
                            UpdateJobSummary();
                        }
                    });

                    string ffmpeg = await VmafRunner.ResolveFfmpegAsync(FfmpegPath, Services.Processes, token).ConfigureAwait(false);
                    var options = new VmafOptions { FfmpegPath = ffmpeg, Backend = backend, Model = model, ProcessRunner = Services.Processes };
                    report = await VmafRunner.RunAsync(reference, entry.Path, options, progress, token).ConfigureAwait(false);
                }
                finally
                {
                    if (cpu)
                    {
                        _cpuScoreGate.Release();
                    }
                }
            },
        });

        entry.ActiveJob = job;
        await job.Completion.ConfigureAwait(true);
        if (!ReferenceEquals(entry.ActiveJob, job))
        {
            return; // re-queued or removed meanwhile
        }

        entry.ActiveJob = null;

        if (job.State == JobState.Succeeded && string.Equals(reference, ReferencePath, StringComparison.Ordinal))
        {
            entry.Report = report; // a finished score job set it
            entry.Progress = 1;
            entry.State = EncodeState.Done;

            if (ReferenceEquals(entry, SelectedEncode))
            {
                NotifyReportChanged();
            }

            UpdateModelConsistency();
            OnPropertyChanged(nameof(Encodes));

            string note = ScoreNote(entry);
            if (note.Length > 0)
            {
                Status = entry.Name + ":" + note;
            }
        }
        else if (job.State == JobState.Cancelled)
        {
            entry.State = EncodeState.Cancelled;
        }
        else if (job.State == JobState.Failed)
        {
            entry.State = EncodeState.Failed;
            entry.Error = FirstLine(job.Error!); // a failed job always says why
            Toast(ToastKind.Error, Loc.T("compare.toast.scoreFailed", entry.Name, entry.Error));
        }

        UpdateJobSummary();
        OnPropertyChanged(nameof(HasStaleEncodes));
    }

    private void CancelJob(EncodeEntry entry)
    {
        if (entry.ActiveJob is { } job)
        {
            entry.ActiveJob = null;
            _jobs.Cancel(job);
        }
    }

    /// <summary>Summarises every row into the status-bar progress and Cancel button.</summary>
    private void UpdateJobSummary()
    {
        List<EncodeEntry> busy = Encodes.Where(e => e.IsWorking || e.State == EncodeState.Queued).ToList();
        IsBusy = busy.Count > 0;

        if (!IsBusy)
        {
            ProgressPercent = 0;
            ProgressDetail = string.Empty;
            int done = Encodes.Count(e => e.IsDone);
            if (done > 0 && Encodes.All(e => !e.IsBusy))
            {
                int failed = Encodes.Count(e => e.IsFailed);
                Status = Loc.T("compare.status.scored", done, ReferenceName) +
                         (failed > 0 ? Loc.T("compare.status.failedCount", failed) : string.Empty) +
                         (SelectedEncode is { } selected ? ScoreNote(selected) : string.Empty);
            }

            return;
        }

        List<EncodeEntry> scoring = busy.Where(e => e.State == EncodeState.Scoring).ToList();
        ProgressIndeterminate = scoring.Count == 0;
        ProgressPercent = scoring.Count == 0 ? 0 : scoring.Average(e => e.Progress) * 100;

        int finished = Encodes.Count(e => e.IsDone);
        int total = finished + busy.Count;
        ProgressDetail = scoring.Count switch
        {
            0 => Loc.T("compare.progress.preparing", busy.Count),
            1 => Loc.T("compare.progress.scoringOne", scoring[0].Name, finished + 1, total),
            _ => Loc.T("compare.progress.scoringMany", scoring.Count, finished + scoring.Count, total),
        };
        Status = ProgressDetail;
    }

    private void UpdateWarnings()
    {
        if (_referenceDecoder is null)
        {
            return;
        }

        foreach (EncodeEntry entry in Encodes.Where(e => e.Width > 0))
        {
            entry.Warning = DescribeMismatch(_referenceDecoder.Info, entry);
        }
    }

    /// <summary>Resolution, frame-rate and exact frame-count differences; scaling is not implemented yet.</summary>
    private string? DescribeMismatch(VideoInfo reference, EncodeEntry entry) =>
        StreamMismatch.Describe(
            new StreamShape(reference.Width, reference.Height, reference.FrameRate, _referencePacketCount),
            new StreamShape(entry.Width, entry.Height, entry.FrameRate, entry.PacketCount));

    private void Toast(ToastKind kind, string message, string? actionLabel = null, Action? action = null) =>
        ToastRequested?.Invoke(this, new ToastRequest(kind, message, actionLabel, action));

    internal static string FirstLine(string text)
    {
        int newline = text.IndexOfAny(['\r', '\n']);
        return newline >= 0 ? text[..newline] : text;
    }

    public void StepFrame(int delta)
    {
        if (Frames.Count == 0)
        {
            return;
        }

        CurrentFrame = Math.Clamp(CurrentFrame + delta, 0, Frames.Count - 1);
    }

    public void GoToFrame(int index)
    {
        if (Frames.Count == 0)
        {
            return;
        }

        CurrentFrame = Math.Clamp(index, 0, Frames.Count - 1);
    }

    public void TogglePlayPause()
    {
        if (IsPlaying)
        {
            StopPlayback();
        }
        else
        {
            StartPlayback();
        }
    }

    public void StartPlayback()
    {
        if (!CanPlay)
        {
            return;
        }

        _playTimer ??= new DispatcherTimer();
        _playTimer.Interval = TimeSpan.FromSeconds(1.0 / Math.Max(1.0, FrameRate));
        _playTimer.Tick -= OnPlayTick;
        _playTimer.Tick += OnPlayTick;
        _playStartFrame = CurrentFrame;
        _playClock.Restart();
        _playTimer.Start();

        IsPlaying = true;
    }

    public void StopPlayback()
    {
        _playTimer?.Stop();
        _playClock.Stop();
        IsPlaying = false;
    }

    public void SetFlicker(bool showDistorted)
    {
        if (IsFlicker)
        {
            FlickerShowsDistorted = showDistorted;
        }
    }

    private void UpdateModelConsistency()
    {
        List<string> models = Encodes
            .Where(e => e.Report is not null)
            .Select(e => e.Report!.Model)
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        bool mixed = models.Count > 1;
        if (mixed == HasMixedModels)
        {
            return;
        }

        HasMixedModels = mixed;
        OnPropertyChanged(nameof(HasMixedModels));

        if (mixed)
        {
            Status = Loc.T("compare.status.mixedModels", string.Join(", ", models.Select(VmafModels.ShortName)));
        }
    }

    public void RemoveEncode(EncodeEntry entry)
    {
        int index = Encodes.IndexOf(entry);
        if (index < 0)
        {
            return;
        }

        CancelJob(entry);
        Encodes.Remove(entry);
        UpdateModelConsistency();
        UpdateJobSummary();

        if (ReferenceEquals(entry, SelectedEncode))
        {
            SelectedEncode = Encodes.Count == 0 ? null : Encodes[Math.Min(index, Encodes.Count - 1)];
        }
    }

    partial void OnSelectedEncodeChanged(EncodeEntry? value)
    {
        NotifyReportChanged();

        _ = OpenSelectedEncodeAsync(value);
    }

    partial void OnReferenceBadgeChanged(string value) => OnPropertyChanged(nameof(HasReferenceBadge));

    partial void OnEncodeBadgeChanged(string value) => OnPropertyChanged(nameof(HasEncodeBadge));

    partial void OnCompareModeChanged(CompareMode value)
    {
        ScheduleSave(s => s.CompareMode = value);
        OnPropertyChanged(nameof(IsTwoPane));
        OnPropertyChanged(nameof(IsSinglePane));
        OnPropertyChanged(nameof(IsWipe));
        OnPropertyChanged(nameof(IsFlicker));
        OnPropertyChanged(nameof(IsDifference));
        OnPropertyChanged(nameof(IsAutoFlicker));
        OnPropertyChanged(nameof(RightPaneTitle));
        OnPropertyChanged(nameof(SinglePaneTitle));
        OnPropertyChanged(nameof(SingleBitmap));

        if (value != CompareMode.Flicker)
        {
            AutoFlicker = false;
            FlickerShowsDistorted = false;
        }

        if (value == CompareMode.Difference)
        {
            RebuildDifferenceImage();
        }
    }

    partial void OnDifferenceGainChanged(double value)
    {
        OnPropertyChanged(nameof(RightPaneTitle));
        OnPropertyChanged(nameof(DifferenceLegend));
        RebuildDifferenceImage();
    }

    partial void OnDifferenceChannelsChanged(DifferenceChannels value)
    {
        OnPropertyChanged(nameof(RightPaneTitle));
        OnPropertyChanged(nameof(DifferenceLegend));
        RebuildDifferenceImage();
    }

    partial void OnFlickerShowsDistortedChanged(bool value) => OnPropertyChanged(nameof(SingleBitmap));

    partial void OnAutoFlickerChanged(bool value)
    {
        OnPropertyChanged(nameof(IsAutoFlicker));

        if (!value)
        {
            _flickerTimer?.Stop();
            FlickerShowsDistorted = false;
            return;
        }

        _flickerTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(240) };
        _flickerTimer.Tick -= OnFlickerTick;
        _flickerTimer.Tick += OnFlickerTick;
        _flickerTimer.Start();
    }

    partial void OnCurrentFrameChanged(int value)
    {
        OnPropertyChanged(nameof(CurrentFrameValue));

        if (value >= 0 && value < Frames.Count)
        {
            CurrentScore = Frames[value].Vmaf;
        }

        OnPropertyChanged(nameof(FrameText));
        _ = LoadFrameAsync();
    }

    partial void OnIsPlayingChanged(bool value) => PlayPauseHint = Loc.T(value ? "compare.pause" : "compare.play");

    partial void OnIsBusyChanged(bool value) => AnalyzeCommand.NotifyCanExecuteChanged();

    partial void OnReferenceNameChanged(string value)
    {
        OnPropertyChanged(nameof(ComparisonTitle));
        OnPropertyChanged(nameof(WindowTitle));
    }

    partial void OnReferencePathChanged(string? value)
    {
        OnPropertyChanged(nameof(ComparisonTitle));
        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(HasReference));
        OnPropertyChanged(nameof(IsEmpty));
        AnalyzeCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedBackendChanged(VmafBackend value)
    {
        OnPropertyChanged(nameof(BackendChip));
        ScheduleSave(s => s.Backend = value);
    }

    partial void OnSelectedModelChanged(string value)
    {
        OnPropertyChanged(nameof(ModelChip));
        ScheduleSave(s => s.Model = value);
    }

    partial void OnFfmpegPathChanged(string value)
    {
        _ = ProbeBackendAsync();

        // Back to "ffmpeg" (or empty) means "resolve automatically": forget the saved path.
        ScheduleSave(s => s.FfmpegPath =
            string.IsNullOrWhiteSpace(value) || string.Equals(value.Trim(), "ffmpeg", StringComparison.Ordinal)
                ? null
                : value.Trim());
    }

    partial void OnFixedScaleChanged(bool value) => ScheduleSave(s => s.FixedScale = value);

    partial void OnShowSmoothedChanged(bool value) => ScheduleSave(s => s.ShowSmoothed = value);

    [RelayCommand(CanExecute = nameof(CanAnalyze))]
    private Task AnalyzeAsync() => SelectedEncode is { } entry ? RescoreAsync(entry) : Task.CompletedTask;

    /// <summary>Re-scores one row (hover action, or the toolbar for the selected row).</summary>
    public Task RescoreAsync(EncodeEntry entry)
    {
        if (!HasReference)
        {
            Status = Loc.T("compare.status.needReference");
            return Task.CompletedTask;
        }

        return ProcessEncodeAsync(entry, indexFirst: entry.PacketCount <= 0);
    }

    [RelayCommand]
    private void RescoreStale()
    {
        foreach (EncodeEntry entry in Encodes.Where(e => e.IsStale || e.IsFailed).ToList())
        {
            _ = RescoreAsync(entry);
        }
    }

    private bool CanAnalyze() => SelectedEncode is { IsBusy: false } && HasReference;

    private static string ScoreNote(EncodeEntry entry)
    {
        if (entry.Report is not { } report)
        {
            return string.Empty;
        }

        if (report.FellBackToCpu)
        {
            return " " + Loc.T("compare.note.cudaFallback", FirstLine(report.FallbackReason!)); // a fallback always has a reason
        }

        if (report.ConvertedTo8Bit)
        {
            return " " + Loc.T("compare.note.converted8Bit");
        }

        return string.Empty;
    }

    [RelayCommand]
    private void CancelAnalysis()
    {
        foreach (EncodeEntry entry in Encodes.Where(e => e.ActiveJob is not null))
        {
            _jobs.Cancel(entry.ActiveJob!);
        }
    }

    [RelayCommand]
    private void AutoGain()
    {
        if (_lastReferenceFrame is not { } reference || _lastDistortedFrame is not { } distorted)
        {
            return;
        }

        double percentile = PixelDiff.DifferencePercentile(reference, distorted, 0.999, DifferenceChannels);
        DifferenceGain = percentile < 0.5 ? 16 : Math.Clamp(Math.Round(200.0 / percentile, 1), 1, 32);
    }

    [RelayCommand(CanExecute = nameof(CanTogglePlayback))]
    private void TogglePlayback() => TogglePlayPause();

    private bool CanTogglePlayback() => CanPlay;

    /// <summary>
    /// Clock-based playback: the target frame comes from elapsed wall time, so a slow decode drops
    /// frames instead of making playback run in slow motion.
    /// </summary>
    private void OnPlayTick(object? sender, EventArgs e)
    {
        if (Frames.Count == 0 || FrameRate <= 0)
        {
            StopPlayback();
            return;
        }

        int offset = (int)Math.Floor(_playClock.Elapsed.TotalSeconds * FrameRate);
        int target = _playStartFrame + offset;

        if (target >= Frames.Count)
        {
            target %= Frames.Count;
            _playStartFrame = target;
            _playClock.Restart();
        }

        if (target != CurrentFrame)
        {
            CurrentFrame = target;
        }
    }

    // Auto-flicker only runs in flicker mode: leaving the mode turns it off.
    private void OnFlickerTick(object? sender, EventArgs e) => FlickerShowsDistorted = !FlickerShowsDistorted;

    private async Task OpenSelectedEncodeAsync(EncodeEntry? entry)
    {
        await _decodeLock.WaitAsync().ConfigureAwait(true);
        try
        {
            _distortedDecoder?.Dispose();
            _distortedDecoder = null;
            _lastDistortedFrame = null;
            DifferenceBitmap = null;

            if (entry is not null)
            {
                string path = entry.Path;
                _distortedDecoder = await Task.Run(() => FrameDecoder.Open(path)).ConfigureAwait(true);

                VideoInfo info = _distortedDecoder.Info;
                DistortedInfo = Describe(entry.Name, info);
                EncodeName = entry.Name;
                entry.BitDepth = info.BitDepth;
                EncodeBadge = info.IsHighBitDepth ? $"{info.BitDepth}-bit" : string.Empty;

                if (_referenceDecoder is not null)
                {
                    CheckColorMetadata(_referenceDecoder.Info, info);
                }
            }
            else
            {
                DistortedInfo = Loc.T("compare.noEncode");
                EncodeName = Loc.T("compare.encodeName");
                EncodeBadge = string.Empty;
            }
        }
        catch (Exception ex)
        {
            DistortedInfo = Loc.T("compare.noEncode");
            Status = Loc.T("compare.status.encodeFailed", ex.Message);
        }
        finally
        {
            _decodeLock.Release();
        }

        await LoadFrameAsync().ConfigureAwait(true);
    }

    private void CheckColorMetadata(VideoInfo reference, VideoInfo encode)
    {
        if (!string.IsNullOrEmpty(reference.ColorSpace) &&
            !string.IsNullOrEmpty(encode.ColorSpace) &&
            !string.Equals(reference.ColorSpace, encode.ColorSpace, StringComparison.Ordinal))
        {
            Status = Loc.T("compare.warning.colorMatrix", reference.ColorSpace, encode.ColorSpace);
        }
        else if (!string.IsNullOrEmpty(reference.ColorRange) &&
                 !string.IsNullOrEmpty(encode.ColorRange) &&
                 !string.Equals(reference.ColorRange, encode.ColorRange, StringComparison.Ordinal))
        {
            Status = Loc.T("compare.warning.colorRange", reference.ColorRange, encode.ColorRange);
        }
    }

    private async Task LoadFrameAsync()
    {
        FrameDecoder? reference = _referenceDecoder;
        FrameDecoder? distorted = _distortedDecoder;
        if (reference is null || distorted is null)
        {
            return;
        }

        int version = Interlocked.Increment(ref _loadVersion);
        int index = Math.Max(0, CurrentFrame);
        bool differenceReady = false;

        await _decodeLock.WaitAsync().ConfigureAwait(true);
        try
        {
            if (version != _loadVersion)
            {
                return;
            }

            // The two decoders are independent, so decode them concurrently; the difference image
            // is built on the same background task instead of the UI thread.
            bool difference = IsDifference;
            double gain = DifferenceGain;
            DifferenceChannels channels = DifferenceChannels;

            (DecodedFrame Reference, DecodedFrame Distorted, DecodedFrame? Difference) frames = await Task.Run(async () =>
            {
                Task<DecodedFrame> pending = Task.Run(() => reference.DecodeFrame(index));
                DecodedFrame d = distorted.DecodeFrame(index);
                DecodedFrame r = await pending.ConfigureAwait(false);
                DecodedFrame? diff = difference && r.Width == d.Width && r.Height == d.Height
                    ? PixelDiff.Difference(r, d, gain, channels)
                    : null;
                return (r, d, diff);
            }).ConfigureAwait(true);

            if (version != _loadVersion)
            {
                return;
            }

            _lastReferenceFrame = frames.Reference;
            _lastDistortedFrame = frames.Distorted;

            ReferenceBitmap = _referenceBitmaps.Present(frames.Reference);
            DistortedBitmap = _distortedBitmaps.Present(frames.Distorted);

            // Settings changed while decoding: fall through to a fresh rebuild below.
            if (frames.Difference is { } built && IsDifference && gain == DifferenceGain && channels == DifferenceChannels)
            {
                DifferenceBitmap = _differenceBitmaps.Present(built);
                differenceReady = true;
            }
        }
        catch (Exception ex)
        {
            Status = Loc.T("compare.status.decodeFailed", ex.Message);
        }
        finally
        {
            _decodeLock.Release();
        }

        if (!differenceReady)
        {
            RebuildDifferenceImage();
        }

        OnPropertyChanged(nameof(SingleBitmap));
    }

    private void RebuildDifferenceImage()
    {
        if (!IsDifference)
        {
            return;
        }

        if (_lastReferenceFrame is not { } reference || _lastDistortedFrame is not { } distorted)
        {
            DifferenceBitmap = null;
            return;
        }

        try
        {
            DecodedFrame difference = PixelDiff.Difference(reference, distorted, DifferenceGain, DifferenceChannels);
            DifferenceBitmap = _differenceBitmaps.Present(difference);
        }
        catch (ArgumentException)
        {
            DifferenceBitmap = null;
        }
    }

    private void NotifyReportChanged()
    {
        OnPropertyChanged(nameof(Report));
        OnPropertyChanged(nameof(Frames));
        OnPropertyChanged(nameof(FrameCount));
        OnPropertyChanged(nameof(MaxFrame));
        OnPropertyChanged(nameof(HasFrames));
        OnPropertyChanged(nameof(StatsText));
        OnPropertyChanged(nameof(FrameText));
        OnPropertyChanged(nameof(CanPlay));
        OnPropertyChanged(nameof(Encodes));
        TogglePlaybackCommand.NotifyCanExecuteChanged();
        AnalyzeCommand.NotifyCanExecuteChanged();

        if (Frames.Count == 0)
        {
            CurrentScore = 0;
        }
        else if (CurrentFrame >= Frames.Count)
        {
            CurrentFrame = 0;
        }
        else
        {
            CurrentScore = Frames[CurrentFrame].Vmaf;
            OnPropertyChanged(nameof(FrameText));
        }
    }

    private void OnEncodesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasEncodes));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(ComparisonTitle));
        OnPropertyChanged(nameof(HasStaleEncodes));
        OnPropertyChanged(nameof(Encodes));
        AnalyzeCommand.NotifyCanExecuteChanged();
    }

    private async Task ProbeBackendAsync()
    {
        string path = string.IsNullOrWhiteSpace(FfmpegPath) ? "ffmpeg" : FfmpegPath.Trim();

        // The probe reports a broken ffmpeg in its capabilities rather than throwing.
        VmafCapabilities capabilities = await VmafRunner.ProbeAsync(path, Services.Processes).ConfigureAwait(true);
        _capabilities = capabilities;
        OnPropertyChanged(nameof(BackendChip));
        LogPath = VqLog.CurrentPath ?? string.Empty;
        BackendStatus = capabilities.Describe();
        if (!capabilities.HasLibvmaf && !capabilities.HasLibvmafCuda)
        {
            BackendStatus += " — " + Loc.T("compare.backend.noVmaf");
        }
    }

    private static string Describe(string name, VideoInfo info) =>
        string.Format(
            CultureInfo.InvariantCulture,
            "{0}   ·   {1}×{2}   ·   {3}   ·   {4:0.###} fps   ·   {5:mm\\:ss\\.f}",
            name,
            info.Width,
            info.Height,
            info.Codec,
            info.FrameRate,
            info.Duration);

    public void Dispose()
    {
        CancelAnalysis();
        _playTimer?.Stop();
        _flickerTimer?.Stop();
        _ = ReleaseDecodersAsync();
    }

    /// <summary>
    /// Frees the decoders and bitmaps once no decode is running: a frame may still be decoding on
    /// a worker thread (with the native decoder) when the window closes. The lock is kept, so
    /// nothing decodes afterwards.
    /// </summary>
    internal async Task ReleaseDecodersAsync()
    {
        await _decodeLock.WaitAsync().ConfigureAwait(true);
        _referenceDecoder?.Dispose();
        _distortedDecoder?.Dispose();
        _referenceBitmaps.Dispose();
        _distortedBitmaps.Dispose();
        _differenceBitmaps.Dispose();
    }
}
