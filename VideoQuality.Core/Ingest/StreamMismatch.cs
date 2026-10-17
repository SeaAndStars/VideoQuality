using System.Globalization;
using VideoQuality.Core.Localization;

namespace VideoQuality.Core.Ingest;

/// <summary>Geometry, rate and length of one stream, as compared between reference and encode.</summary>
/// <param name="Frames">
/// Exact frame count from a packet walk, or 0 when not known yet. Never pass a container estimate:
/// Matroska stores no frame count, and duration × fps (1440.032 s × 23.976 = 34526.2) is off by one
/// for a file that really has 34525 frames.
/// </param>
public readonly record struct StreamShape(int Width, int Height, double FrameRate, int Frames);

/// <summary>Warns when an encode cannot be compared frame for frame with its reference.</summary>
public static class StreamMismatch
{
    public static string? Describe(StreamShape reference, StreamShape encode)
    {
        var problems = new List<string>();

        if (encode.Width > 0 && reference.Width > 0 &&
            (encode.Width != reference.Width || encode.Height != reference.Height))
        {
            problems.Add(Loc.T("mismatch.resolution", encode.Width, encode.Height, reference.Width, reference.Height));
        }

        if (encode.FrameRate > 0 && reference.FrameRate > 0 && Math.Abs(encode.FrameRate - reference.FrameRate) > 0.01)
        {
            problems.Add(Loc.T("mismatch.frameRate", encode.FrameRate, reference.FrameRate));
        }

        // Only exact counts are compared; an unknown count is not a mismatch.
        if (encode.Frames > 0 && reference.Frames > 0 && encode.Frames != reference.Frames)
        {
            problems.Add(Loc.T("mismatch.frames", encode.Frames, reference.Frames));
        }

        return problems.Count == 0 ? null : Loc.T("mismatch.prefix", string.Join("; ", problems));
    }
}
