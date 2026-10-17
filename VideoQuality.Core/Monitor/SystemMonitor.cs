using System.Globalization;
using System.Runtime.InteropServices;
using VideoQuality.Core.Tools;

namespace VideoQuality.Core.Monitor;

/// <summary>One NVIDIA GPU's load (from nvidia-smi).</summary>
public sealed record GpuSample(string Name, double Utilization, double Encoder, double Decoder, double MemoryUsedMb, double MemoryTotalMb, double Temperature);

/// <summary>Machine load at one moment.</summary>
public sealed record SystemSample(DateTime Time, double CpuPercent, long MemoryUsed, long MemoryTotal, IReadOnlyList<GpuSample> Gpus);

/// <summary>
/// Samples CPU, memory and NVIDIA GPU load for the Monitor page. CPU usage is the change between two
/// samples, so the first sample after construction reads 0.
/// </summary>
public sealed partial class SystemMonitor(IProcessRunner runner, string nvidiaSmi = "nvidia-smi")
{
    private (ulong Idle, ulong Total)? _lastCpu;

    public const string NvidiaQuery = "--query-gpu=name,utilization.gpu,utilization.encoder,utilization.decoder,memory.used,memory.total,temperature.gpu";

    public async Task<SystemSample> SampleAsync(CancellationToken cancellationToken = default)
    {
        (ulong idle, ulong total) = OperatingSystem.IsWindows() ? WindowsCpuTimes() : ParseProcStat(await File.ReadAllTextAsync("/proc/stat", cancellationToken).ConfigureAwait(false));
        double cpu = 0;
        if (_lastCpu is var (lastIdle, lastTotal) && total > lastTotal)
        {
            cpu = 100.0 * (1 - (double)(idle - lastIdle) / (total - lastTotal));
        }

        _lastCpu = (idle, total);
        (long used, long all) = OperatingSystem.IsWindows() ? WindowsMemory() : ParseMemInfo(await File.ReadAllTextAsync("/proc/meminfo", cancellationToken).ConfigureAwait(false));
        return new SystemSample(DateTime.UtcNow, Math.Clamp(cpu, 0, 100), used, all, await GpusAsync(cancellationToken).ConfigureAwait(false));
    }

    private async Task<IReadOnlyList<GpuSample>> GpusAsync(CancellationToken cancellationToken)
    {
        try
        {
            ProcessResult result = await runner.RunAsync(
                new ProcessRequest { FileName = nvidiaSmi, Arguments = [NvidiaQuery, "--format=csv,noheader,nounits"] }, cancellationToken).ConfigureAwait(false);
            return result.ExitCode == 0 ? ParseNvidiaSmi(result.Output) : [];
        }
        catch (ProcessStartException)
        {
            return []; // no NVIDIA driver
        }
    }

    /// <summary>The aggregate <c>cpu</c> line of /proc/stat: (idle + iowait, total) jiffies.</summary>
    public static (ulong Idle, ulong Total) ParseProcStat(string text)
    {
        string line = text.Split('\n').First(l => l.StartsWith("cpu ", StringComparison.Ordinal));
        ulong[] values = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(v => ulong.Parse(v, CultureInfo.InvariantCulture)).ToArray();
        ulong idle = values[3] + (values.Length > 4 ? values[4] : 0);
        return (idle, values.Aggregate(0UL, (sum, v) => sum + v));
    }

    /// <summary>(used, total) bytes from /proc/meminfo, used = total − available.</summary>
    public static (long Used, long Total) ParseMemInfo(string text)
    {
        long Kilobytes(string key) => text.Split('\n')
            .Where(l => l.StartsWith(key + ":", StringComparison.Ordinal))
            .Select(l => long.Parse(l.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1], CultureInfo.InvariantCulture))
            .FirstOrDefault();
        long total = Kilobytes("MemTotal") * 1024;
        return (total - (Kilobytes("MemAvailable") * 1024), total);
    }

    /// <summary>Rows of <see cref="NvidiaQuery"/> output; "[N/A]" fields read as 0.</summary>
    public static IReadOnlyList<GpuSample> ParseNvidiaSmi(string csv)
    {
        static double Number(string value) =>
            double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double number) ? number : 0;

        return csv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split(','))
            .Where(fields => fields.Length >= 7)
            .Select(f => new GpuSample(f[0].Trim(), Number(f[1]), Number(f[2]), Number(f[3]), Number(f[4]), Number(f[5]), Number(f[6])))
            .ToList();
    }

    private static (ulong Idle, ulong Total) WindowsCpuTimes()
    {
        GetSystemTimes(out long idle, out long kernel, out long user);
        return ((ulong)idle, (ulong)(kernel + user)); // kernel time includes idle time
    }

    private static (long Used, long Total) WindowsMemory()
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        GlobalMemoryStatusEx(ref status);
        return ((long)(status.TotalPhys - status.AvailPhys), (long)status.TotalPhys);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [LibraryImport("kernel32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemTimes(out long idle, out long kernel, out long user);

    [LibraryImport("kernel32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatus status);
}
