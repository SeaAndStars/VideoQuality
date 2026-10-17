using System.Text.Json;
using VideoQuality.Core.Models;
using VideoQuality.Core.Reporting;
using VideoQuality.Core.Scoring;

namespace VideoQuality.Tests;

public class VmafReportExporterTests
{
    private static VmafReport CreateReport()
    {
        var frames = new[]
        {
            new VmafFrame(0, 95.5),
            new VmafFrame(1, 90.25),
        };

        return new VmafReport(
            frames,
            VmafStats.FromFrames(frames),
            VmafExecutionBackend.Cuda,
            "ffmpeg -i a -i b -lavfi libvmaf",
            TimeSpan.FromSeconds(1.5))
        {
            SourceBitDepth = 10,
            ConvertedTo8Bit = true,
            Model = "vmaf_v0.6.1",
        };
    }

    [Fact]
    public void Csv_has_a_header_and_one_row_per_frame()
    {
        string csv = VmafReportExporter.ToCsv(CreateReport());
        string[] lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("frame,vmaf", lines[0]);
        Assert.Equal(3, lines.Length);
        Assert.StartsWith("0,95.5", lines[1]);
        Assert.StartsWith("1,90.25", lines[2]);
    }

    [Fact]
    public void Json_carries_backend_precision_and_stats()
    {
        string json = VmafReportExporter.ToJson(CreateReport(), "ref.mkv", "dist.mp4");

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        Assert.Equal("ref.mkv", root.GetProperty("reference").GetString());
        Assert.Equal("dist.mp4", root.GetProperty("distorted").GetString());
        Assert.Equal("CUDA", root.GetProperty("backend").GetString());
        Assert.True(root.GetProperty("convertedTo8Bit").GetBoolean());
        Assert.Equal(10, root.GetProperty("sourceBitDepth").GetInt32());
        Assert.False(root.GetProperty("fellBackToCpu").GetBoolean());
        Assert.Equal("vmaf_v0.6.1", root.GetProperty("model").GetString());
        Assert.Equal(2, root.GetProperty("frames").GetArrayLength());
        Assert.Equal(95.5, root.GetProperty("frames")[0].GetProperty("vmaf").GetDouble(), 6);
        Assert.Equal(2, root.GetProperty("stats").GetProperty("count").GetInt32());
    }

    [Fact]
    public void Precision_labels_distinguish_converted_runs()
    {
        VmafReport converted = CreateReport();
        Assert.Equal("CUDA·10b→8b", converted.PrecisionLabel);
        Assert.Equal("CUDA·8b", converted.PrecisionShort);

        VmafReport native = converted with { ConvertedTo8Bit = false, Backend = VmafExecutionBackend.Cpu };
        Assert.Equal("CPU", native.PrecisionLabel);
        Assert.Equal("CPU", native.PrecisionShort);
    }

    [Fact]
    public void Files_are_written_and_fallbacks_recorded()
    {
        string folder = Directory.CreateTempSubdirectory("vq-export-").FullName;
        VmafReport report = CreateReport() with { FallbackReason = "no CUDA" };
        VmafReportExporter.WriteCsv(Path.Combine(folder, "r.csv"), report);
        VmafReportExporter.WriteJson(Path.Combine(folder, "r.json"), report);
        Assert.StartsWith("frame,vmaf", File.ReadAllText(Path.Combine(folder, "r.csv")), StringComparison.Ordinal);
        Assert.Contains("\"fallbackReason\": \"no CUDA\"", File.ReadAllText(Path.Combine(folder, "r.json")), StringComparison.Ordinal);
        Directory.Delete(folder, true);
    }
}
