using System.Globalization;
using System.Text;
using System.Text.Json;
using VideoQuality.Core.Models;
using VideoQuality.Core.Scoring;

namespace VideoQuality.Core.Reporting;

/// <summary>
/// Serialises a <see cref="VmafReport"/> for downstream tools. CSV is just the curve; JSON keeps the
/// provenance needed to interpret it later (backend, precision, model, command, stats).
/// </summary>
public static class VmafReportExporter
{
    // JSON must not carry a BOM (RFC 8259); CSV is cleaner without one too.
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static string ToCsv(VmafReport report)
    {
        var builder = new StringBuilder("frame,vmaf\n");

        foreach (VmafFrame frame in report.Frames)
        {
            builder.Append(frame.FrameNum.ToString(CultureInfo.InvariantCulture))
                .Append(',')
                .Append(frame.Vmaf.ToString("F6", CultureInfo.InvariantCulture))
                .Append('\n');
        }

        return builder.ToString();
    }

    public static void WriteCsv(string path, VmafReport report) =>
        File.WriteAllText(path, ToCsv(report), Utf8NoBom);

    public static string ToJson(VmafReport report, string? reference = null, string? distorted = null)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();

            if (reference is not null)
            {
                writer.WriteString("reference", reference);
            }

            if (distorted is not null)
            {
                writer.WriteString("distorted", distorted);
            }

            writer.WriteString("backend", report.Backend.ToDisplayName());
            writer.WriteBoolean("convertedTo8Bit", report.ConvertedTo8Bit);
            writer.WriteNumber("sourceBitDepth", report.SourceBitDepth);
            writer.WriteBoolean("fellBackToCpu", report.FellBackToCpu);

            if (!string.IsNullOrEmpty(report.FallbackReason))
            {
                writer.WriteString("fallbackReason", report.FallbackReason);
            }

            writer.WriteString("model", report.Model);
            writer.WriteString("command", report.CommandLine);
            writer.WriteNumber("durationSeconds", Math.Round(report.Duration.TotalSeconds, 3));

            writer.WriteStartObject("stats");
            writer.WriteNumber("count", report.Stats.Count);
            writer.WriteNumber("mean", Math.Round(report.Stats.Mean, 6));
            writer.WriteNumber("harmonicMean", Math.Round(report.Stats.HarmonicMean, 6));
            writer.WriteNumber("p1", Math.Round(report.Stats.P1, 6));
            writer.WriteNumber("min", Math.Round(report.Stats.Min, 6));
            writer.WriteNumber("max", Math.Round(report.Stats.Max, 6));
            writer.WriteNumber("stdDev", Math.Round(report.Stats.StdDev, 6));
            writer.WriteEndObject();

            writer.WriteStartArray("frames");
            foreach (VmafFrame frame in report.Frames)
            {
                writer.WriteStartObject();
                writer.WriteNumber("frameNum", frame.FrameNum);
                writer.WriteNumber("vmaf", Math.Round(frame.Vmaf, 6));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Utf8NoBom.GetString(stream.ToArray());
    }

    public static void WriteJson(string path, VmafReport report, string? reference = null, string? distorted = null) =>
        File.WriteAllText(path, ToJson(report, reference, distorted), Utf8NoBom);
}
