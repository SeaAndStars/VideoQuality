using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using VideoQuality.Core.Encode;
using VideoQuality.Core.Localization;
using VideoQuality.Core.Media;
using VideoQuality.Core.Monitor;

namespace VideoQuality.App.ViewModels;

/// <summary>One measured quantity with its recent history (percent).</summary>
public partial class Gauge(string title) : ObservableObject
{
    private readonly List<double> _history = [];

    public string Title { get; } = title;

    [ObservableProperty]
    public partial string Value { get; private set; } = "–";

    [ObservableProperty]
    public partial IReadOnlyList<double> History { get; private set; } = [];

    public void Add(double percent, string text)
    {
        _history.Add(percent);
        if (_history.Count > 120)
        {
            _history.RemoveAt(0);
        }

        Value = text;
        History = [.. _history];
    }
}

/// <summary>What one running encode is doing.</summary>
public sealed record RunningEncode(string Name, string Details);

/// <summary>Monitor page: machine load while encoding (like a small task manager).</summary>
public partial class MonitorViewModel : ObservableObject
{
    private readonly SystemMonitor _monitor;
    private readonly EncodeQueue _queue;
    private readonly Dictionary<Guid, (DateTime Time, TimeSpan Cpu)> _lastCpu = [];

    public MonitorViewModel(SystemMonitor monitor, EncodeQueue queue)
    {
        _monitor = monitor;
        _queue = queue;
    }

    public Gauge Cpu { get; } = new(Loc.T("monitor.cpu"));

    public Gauge Memory { get; } = new(Loc.T("monitor.memory"));

    public Gauge Gpu { get; } = new(Loc.T("monitor.gpu"));

    public Gauge GpuEncoder { get; } = new(Loc.T("monitor.encoder"));

    public Gauge GpuDecoder { get; } = new(Loc.T("monitor.decoder"));

    public Gauge GpuMemory { get; } = new(Loc.T("monitor.vram"));

    public ObservableCollection<RunningEncode> Encodes { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGpu))]
    public partial string GpuName { get; private set; } = string.Empty;

    public bool HasGpu => GpuName.Length > 0;

    public bool HasEncodes => Encodes.Count > 0;

    /// <summary>Takes one sample and updates every gauge (the page calls this once a second while visible).</summary>
    public async Task SampleAsync(CancellationToken cancellationToken = default)
    {
        SystemSample sample = await _monitor.SampleAsync(cancellationToken);
        Cpu.Add(sample.CpuPercent, Percent(sample.CpuPercent));
        double memory = sample.MemoryTotal > 0 ? 100.0 * sample.MemoryUsed / sample.MemoryTotal : 0;
        Memory.Add(memory, $"{MediaInfo.FormatSize(sample.MemoryUsed)} / {MediaInfo.FormatSize(sample.MemoryTotal)}");
        if (sample.Gpus.FirstOrDefault() is { } gpu)
        {
            GpuName = gpu.Name;
            Gpu.Add(gpu.Utilization, Percent(gpu.Utilization) + $" · {gpu.Temperature:0} °C");
            GpuEncoder.Add(gpu.Encoder, Percent(gpu.Encoder));
            GpuDecoder.Add(gpu.Decoder, Percent(gpu.Decoder));
            double vram = gpu.MemoryTotalMb > 0 ? 100 * gpu.MemoryUsedMb / gpu.MemoryTotalMb : 0;
            GpuMemory.Add(vram, $"{gpu.MemoryUsedMb:0} / {gpu.MemoryTotalMb:0} MiB");
        }

        Encodes.Clear();
        foreach (EncodeJob job in _queue.Jobs.Where(j => j.State is EncodeJobState.Running or EncodeJobState.Paused))
        {
            // CPU use of this ffmpeg since the last sample, in cores.
            TimeSpan cpu = job.ProcessorTime;
            double cores = 0;
            if (_lastCpu.TryGetValue(job.Id, out (DateTime Time, TimeSpan Cpu) last) && sample.Time > last.Time)
            {
                cores = (cpu - last.Cpu).TotalSeconds / (sample.Time - last.Time).TotalSeconds;
            }

            _lastCpu[job.Id] = (sample.Time, cpu);
            Encodes.Add(new RunningEncode(job.Name, Loc.T("monitor.job",
                (job.Progress * 100).ToString("0.0", CultureInfo.InvariantCulture),
                job.Fps.ToString("0.#", CultureInfo.InvariantCulture),
                job.Speed.ToString("0.00", CultureInfo.InvariantCulture),
                cores.ToString("0.0", CultureInfo.InvariantCulture))));
        }

        OnPropertyChanged(nameof(HasEncodes));
    }

    private static string Percent(double value) => value.ToString("0", CultureInfo.InvariantCulture) + " %";
}
