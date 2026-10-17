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
using VideoQuality.Core.Models;
using VideoQuality.Core.Scoring;

namespace VideoQuality.App.ViewModels;

/// <summary>
/// One <see cref="CurrentFrame"/> drives both decoders, the slider, the keyboard, playback and the
/// plot. Encodes are scored against one reference and overlaid on the graph; the selected encode is
/// what the viewer compares against.
/// </summary>
public partial class MainViewModel : ViewModelBase, IDisposable
{
    private static readonly string[] Palette =
    [
        "#4fc3f7", "#ffb74d", "#81c784", "#e57373",
        "#ba68c8", "#ffd54f", "#4db6ac", "#f06292",
    ];

    private readonly SemaphoreSlim _decodeLock = new(1, 1);
    private FrameDecoder? _referenceDecoder;
    private FrameDecoder? _distortedDecoder;
    private int _loadVersion;
    private CancellationTokenSource? _analysisCancellation;
    private DispatcherTimer? _playTimer;
    private readonly Stopwatch _playClock = new();
    private int _playStartFrame;
    private DispatcherTimer? _flickerTimer;
    private readonly BitmapSwapChain _referenceBitmaps = new();
    private readonly BitmapSwapChain _distortedBitmaps = new();
    private readonly BitmapSwapChain _differenceBitmaps = new();
    private DecodedFrame? _lastReferenceFrame;
    private DecodedFrame? _lastDistortedFrame;
    private bool _suppressEncodeOpen;
    private VmafCapabilities? _capabilities;

    public MainViewModel()
    {
        Viewport = new ViewportState();
        Encodes.CollectionChanged += OnEncodesChanged;

        string? configuredFfmpeg = Environment.GetEnvironmentVariable("VQ_FFMPEG");
        if (!string.IsNullOrWhiteSpace(configuredFfmpeg))
        {
            FfmpegPath = configuredFfmpeg;
        }
    }

    public ViewportState Viewport { get; }

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
    public partial string PlayPauseHint { get; set; } = "Play";

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
    public partial string Status { get; set; } = "Pick a reference, then add one or more encodes to compare.";

    [ObservableProperty]
    public partial string BackendStatus { get; set; } = "probing ffmpeg…";

    [ObservableProperty]
    public partial string ReferenceInfo { get; set; } = "no reference";

    [ObservableProperty]
    public partial string DistortedInfo { get; set; } = "no encode";

    [ObservableProperty]
    public partial string ReferenceName { get; set; } = "reference";

    [ObservableProperty]
    public partial string EncodeName { get; set; } = "encode";

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

    public double MaxFrame => Math.Max(0, FrameCount - 1);

    public bool HasEncodes => Encodes.Count > 0;

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
        ? $"DIFFERENCE  ×{DifferenceGain:0.#}  ·  {ChannelLabel}"
        : "DISTORTED";

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
            return $"{channel} 0–{top} · gain {DifferenceGain:0.#}×";
        }
    }

    public string ChannelLabel => DifferenceChannels switch
    {
        DifferenceChannels.Luma => "luma",
        DifferenceChannels.Chroma => "chroma",
        _ => "luma + chroma",
    };

    public string SinglePaneTitle => IsWipe
        ? "WIPE  —  drag the divider"
        : "FLICKER  —  hold F to reveal the encode";

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
                ? $"frame    —  /    —     {label}      —"
                : string.Format(
                    CultureInfo.InvariantCulture,
                    "frame {0,4} / {1,-4}   {2} {3,6:F2}",
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
            return string.Format(
                CultureInfo.InvariantCulture,
                "mean {0,6:F2}   hmean {1,6:F2}   p1 {2,6:F2}   min {3,6:F2}   max {4,6:F2}   σ {5,5:F2}   {6} · {7}",
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
        if (Avalonia.Controls.Design.IsDesignMode)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(FfmpegPath) || string.Equals(FfmpegPath, "ffmpeg", StringComparison.Ordinal))
        {
            string resolved = await VmafRunner.ResolveFfmpegAsync(FfmpegPath).ConfigureAwait(true);
            if (!string.Equals(resolved, FfmpegPath, StringComparison.Ordinal))
            {
                FfmpegPath = resolved;
                return;
            }
        }

        await ProbeBackendAsync().ConfigureAwait(true);
        LogSession();
    }

    private void LogSession()
    {
        VqLog.SessionHeader("app session", new Dictionary<string, string>
        {
            ["core"] = typeof(VmafRunner).Assembly.GetName().Version?.ToString() ?? "?",
            ["dotnet"] = Environment.Version.ToString(),
            ["os"] = RuntimeInformation.OSDescription,
            ["ffmpeg"] = FfmpegPath,
            ["backend"] = _capabilities?.Describe() ?? "unknown",
            ["libav"] = SafeLibav(),
            ["log"] = VqLog.CurrentPath ?? "(disabled)",
        });
    }

    private static string SafeLibav()
    {
        try
        {
            return FfmpegRuntime.Describe();
        }
        catch (Exception ex)
        {
            return "unavailable: " + ex.Message;
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
                ? "Reference loaded. Add one or more encodes."
                : "Reference loaded.";
        }
        catch (Exception ex)
        {
            ReferenceInfo = "no reference";
            Status = "Could not open reference: " + ex.Message;
        }
        finally
        {
            _decodeLock.Release();
        }

        await LoadFrameAsync().ConfigureAwait(true);
    }

    /// <summary>Adds (or re-scores) one or more distorted files against the current reference.</summary>
    public async Task AddEncodesAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(ReferencePath))
        {
            Status = "Pick a reference video first.";
            return;
        }

        if (_referenceDecoder is null)
        {
            await SetReferenceAsync(ReferencePath).ConfigureAwait(true);
        }

        if (_referenceDecoder is null)
        {
            return;
        }

        IsBusy = true;
        _analysisCancellation?.Dispose();
        _analysisCancellation = new CancellationTokenSource();
        CancellationToken token = _analysisCancellation.Token;

        try
        {
            foreach (string path in paths)
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                EncodeEntry entry = Encodes.FirstOrDefault(e => string.Equals(e.Path, path, StringComparison.Ordinal))
                    ?? new EncodeEntry(path, Palette[Encodes.Count % Palette.Length]);

                if (!Encodes.Contains(entry))
                {
                    Encodes.Add(entry);
                }

                _suppressEncodeOpen = true;
                try
                {
                    SelectedEncode = entry;
                }
                finally
                {
                    _suppressEncodeOpen = false;
                }

                // Open first so the bit depth and packet index are known before choosing a backend.
                await OpenSelectedEncodeAsync(entry).ConfigureAwait(true);
                await ScoreEntryAsync(entry, token).ConfigureAwait(true);
            }

            Status = $"Scored {Encodes.Count} encode(s) against {Path.GetFileName(ReferencePath!)}.{ScoreNote(SelectedEncode)}";
        }
        catch (OperationCanceledException)
        {
            Status = "Scoring cancelled.";
        }
        catch (Exception ex)
        {
            Status = "Scoring failed: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
            ProgressPercent = 0;
            ProgressDetail = string.Empty;
            _analysisCancellation?.Dispose();
            _analysisCancellation = null;
        }
    }

    /// <summary>Opens a reference and one or more encodes, then scores them (startup arguments).</summary>
    public async Task OpenAndAnalyzeAsync(string reference, IReadOnlyList<string> encodes)
    {
        await SetReferenceAsync(reference).ConfigureAwait(true);
        await AddEncodesAsync(encodes).ConfigureAwait(true);
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
            Status = "Warning: encodes were scored with different models (" +
                     string.Join(", ", models.Select(VmafModels.ShortName)) +
                     "). Curves are not comparable; re-score them with one model.";
        }
    }

    public void RemoveEncode(EncodeEntry entry)
    {
        int index = Encodes.IndexOf(entry);
        if (index < 0)
        {
            return;
        }

        Encodes.Remove(entry);
        UpdateModelConsistency();

        if (ReferenceEquals(entry, SelectedEncode))
        {
            SelectedEncode = Encodes.Count == 0 ? null : Encodes[Math.Min(index, Encodes.Count - 1)];
        }
    }

    partial void OnSelectedEncodeChanged(EncodeEntry? value)
    {
        NotifyReportChanged();

        if (!_suppressEncodeOpen)
        {
            _ = OpenSelectedEncodeAsync(value);
        }
    }

    partial void OnReferenceBadgeChanged(string value) => OnPropertyChanged(nameof(HasReferenceBadge));

    partial void OnEncodeBadgeChanged(string value) => OnPropertyChanged(nameof(HasEncodeBadge));

    partial void OnCompareModeChanged(CompareMode value)
    {
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

    partial void OnIsPlayingChanged(bool value) => PlayPauseHint = value ? "Pause" : "Play";

    partial void OnIsBusyChanged(bool value) => AnalyzeCommand.NotifyCanExecuteChanged();

    partial void OnFfmpegPathChanged(string value) => _ = ProbeBackendAsync();

    [RelayCommand(CanExecute = nameof(CanAnalyze))]
    private async Task AnalyzeAsync()
    {
        if (SelectedEncode is not { } entry || string.IsNullOrWhiteSpace(ReferencePath))
        {
            return;
        }

        IsBusy = true;
        _analysisCancellation?.Dispose();
        _analysisCancellation = new CancellationTokenSource();

        try
        {
            await ScoreEntryAsync(entry, _analysisCancellation.Token).ConfigureAwait(true);
            Status = $"Re-scored {entry.Name}.{ScoreNote(entry)}";
        }
        catch (OperationCanceledException)
        {
            Status = "Analysis cancelled.";
        }
        catch (Exception ex)
        {
            Status = "Analysis failed: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
            ProgressPercent = 0;
            ProgressDetail = string.Empty;
            _analysisCancellation?.Dispose();
            _analysisCancellation = null;
        }
    }

    private bool CanAnalyze() => SelectedEncode is not null && !IsBusy;

    private static string ScoreNote(EncodeEntry? entry)
    {
        if (entry?.Report is not { } report)
        {
            return string.Empty;
        }

        if (report.FellBackToCpu)
        {
            string reason = report.FallbackReason ?? "unknown error";
            int newline = reason.IndexOfAny(['\r', '\n']);
            if (newline >= 0)
            {
                reason = reason[..newline];
            }

            return $" CUDA scoring failed; fell back to CPU ({reason}).";
        }

        if (report.ConvertedTo8Bit)
        {
            return " 10-bit input was converted to 8-bit on the GPU; choose CPU for full precision.";
        }

        return string.Empty;
    }

    [RelayCommand]
    private void CancelAnalysis() => _analysisCancellation?.Cancel();

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

    private void OnFlickerTick(object? sender, EventArgs e)
    {
        if (IsFlicker)
        {
            FlickerShowsDistorted = !FlickerShowsDistorted;
        }
    }

    private async Task ScoreEntryAsync(EncodeEntry entry, CancellationToken cancellationToken)
    {
        string ffmpeg = await VmafRunner.ResolveFfmpegAsync(FfmpegPath, cancellationToken).ConfigureAwait(true);

        int referenceDepth = _referenceDecoder?.Info.BitDepth ?? 8;
        int encodeDepth = entry.BitDepth > 0 ? entry.BitDepth : 8;
        bool highBitDepth = Math.Max(referenceDepth, encodeDepth) > 8;

        // GPU-first: Auto resolves to libvmaf_cuda whenever it is available, including 10-bit
        // sources. libvmaf_cuda only accepts yuv420p, so the GPU scaler converts to 8-bit there;
        // choose CPU explicitly when 10-bit precision matters more than speed.
        var options = new VmafOptions
        {
            FfmpegPath = ffmpeg,
            Backend = SelectedBackend,
            Model = SelectedModel,
        };

        int totalFrames = _referenceDecoder?.Info.FrameCount ?? 0;
        ProgressIndeterminate = totalFrames <= 0;
        ProgressPercent = 0;

        var progress = new Progress<VmafProgress>(p =>
        {
            if (p.FrameNumber is not int frame)
            {
                return;
            }

            ProgressPercent = totalFrames > 0 ? Math.Min(100, frame * 100.0 / totalFrames) : 0;
            string speed = p.Fps > 0.5 ? $" · {p.Fps:0} fps" : string.Empty;
            ProgressDetail = totalFrames > 0
                ? $"{entry.Name}: {frame}/{totalFrames} frames{speed}"
                : $"{entry.Name}: {frame} frames{speed}";
            Status = ProgressDetail;
        });

        Status = $"Scoring {entry.Name}…";
        VmafReport report = await VmafRunner
            .RunAsync(ReferencePath!, entry.Path, options, progress, cancellationToken)
            .ConfigureAwait(true);

        entry.Report = report;


        // Fallback only; OpenSelectedEncodeAsync normally fills this from the packet index.
        if (entry.BitrateKbps <= 0)
        {
            double seconds = _referenceDecoder?.Info.Duration.TotalSeconds ?? 0;
            entry.BitrateKbps = seconds > 0 ? entry.FileSize * 8.0 / seconds / 1000.0 : 0;
        }

        if (ReferenceEquals(entry, SelectedEncode))
        {
            NotifyReportChanged();
        }

        UpdateModelConsistency();
        OnPropertyChanged(nameof(Encodes));
    }

    private async Task OpenSelectedEncodeAsync(EncodeEntry? entry)
    {
        bool opened = false;

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
                entry.PacketCount = info.FrameCount;
                EncodeBadge = info.IsHighBitDepth ? $"{info.BitDepth}-bit" : string.Empty;
                opened = true;

                if (_referenceDecoder is not null)
                {
                    CheckColorMetadata(_referenceDecoder.Info, info);
                }
            }
            else
            {
                DistortedInfo = "no encode";
                EncodeName = "encode";
                EncodeBadge = string.Empty;
            }
        }
        catch (Exception ex)
        {
            DistortedInfo = "no encode";
            Status = "Could not open encode: " + ex.Message;
        }
        finally
        {
            _decodeLock.Release();
        }

        // Show the first frame before the packet walk, which reads the whole file.
        await LoadFrameAsync().ConfigureAwait(true);

        if (opened && entry is not null)
        {
            // True video bitrate: sum of video packet sizes, not file size (which includes audio
            // and container overhead).
            try
            {
                string path = entry.Path;
                PacketIndex index = await Task.Run(() => PacketIndex.Build(path)).ConfigureAwait(true);
                entry.PacketCount = index.FrameCount;
                if (index.BitrateKbps > 0)
                {
                    entry.BitrateKbps = index.BitrateKbps;
                }
            }
            catch
            {
                // Keep the size/duration estimate as a fallback.
            }
        }
    }

    private void CheckColorMetadata(VideoInfo reference, VideoInfo encode)
    {
        if (!string.IsNullOrEmpty(reference.ColorSpace) &&
            !string.IsNullOrEmpty(encode.ColorSpace) &&
            !string.Equals(reference.ColorSpace, encode.ColorSpace, StringComparison.Ordinal))
        {
            Status = $"Warning: color matrices differ (reference {reference.ColorSpace}, encode {encode.ColorSpace}).";
        }
        else if (!string.IsNullOrEmpty(reference.ColorRange) &&
                 !string.IsNullOrEmpty(encode.ColorRange) &&
                 !string.Equals(reference.ColorRange, encode.ColorRange, StringComparison.Ordinal))
        {
            Status = $"Warning: color ranges differ (reference {reference.ColorRange}, encode {encode.ColorRange}).";
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
            Status = "Decode failed: " + ex.Message;
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
        OnPropertyChanged(nameof(Encodes));
        AnalyzeCommand.NotifyCanExecuteChanged();
    }

    private async Task ProbeBackendAsync()
    {
        string path = string.IsNullOrWhiteSpace(FfmpegPath) ? "ffmpeg" : FfmpegPath.Trim();

        try
        {
            VmafCapabilities capabilities = await VmafRunner.ProbeAsync(path).ConfigureAwait(true);
            _capabilities = capabilities;
            LogPath = VqLog.CurrentPath ?? string.Empty;
            BackendStatus = capabilities.Describe();
            if (!capabilities.HasLibvmaf && !capabilities.HasLibvmafCuda)
            {
                BackendStatus += " — this ffmpeg has no VMAF";
            }
        }
        catch (Exception ex)
        {
            BackendStatus = "probe failed: " + ex.Message;
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
        _analysisCancellation?.Cancel();
        _analysisCancellation?.Dispose();
        _playTimer?.Stop();
        _flickerTimer?.Stop();
        _referenceDecoder?.Dispose();
        _distortedDecoder?.Dispose();
        _decodeLock.Dispose();
        _referenceBitmaps.Dispose();
        _distortedBitmaps.Dispose();
        _differenceBitmaps.Dispose();
    }
}
