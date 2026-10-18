using System.Globalization;

namespace VideoQuality.Core.Scoring;

/// <summary>
/// Parses the <c>key=value</c> stream produced by <c>ffmpeg -progress pipe:1</c>. Pure and
/// side-effect free so it can be unit tested without ffmpeg.
/// </summary>
public sealed class FfmpegProgressParser
{
    private int _frame = -1;
    private double _fps;

    /// <summary>Last frame number seen, or null when ffmpeg has not reported one yet.</summary>
    public int? Frame => _frame >= 0 ? _frame : null;

    public double Fps => _fps;

    /// <summary>Position reached in the output (<c>out_time_us</c>).</summary>
    public TimeSpan OutTime { get; private set; }

    /// <summary>
    /// Whether ffmpeg reported a position. It says N/A throughout when an output stream (a copied
    /// subtitle or font attachment) never gets a packet.
    /// </summary>
    public bool HasOutTime { get; private set; }

    /// <summary>Bytes written so far (<c>total_size</c>).</summary>
    public long TotalSize { get; private set; }

    /// <summary>Realtime factor (<c>speed=1.5x</c>), 0 until known.</summary>
    public double Speed { get; private set; }

    /// <summary>
    /// Consumes one line. Returns a progress value when the line terminates a block
    /// (<c>progress=continue</c> or <c>progress=end</c>), otherwise null.
    /// </summary>
    public VmafProgress? Consume(string? line)
    {
        if (line is null)
        {
            return null;
        }

        int separator = line.IndexOf('=');
        if (separator <= 0)
        {
            return null;
        }

        string key = line[..separator].Trim();
        string value = line[(separator + 1)..].Trim();

        switch (key)
        {
            case "frame":
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int frame))
                {
                    _frame = frame;
                }

                return null;

            case "fps":
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double fps))
                {
                    _fps = fps;
                }

                return null;

            case "out_time_us" or "out_time_ms":
                // Both are microseconds (out_time_ms is a long-standing misnomer); "N/A" before the first frame.
                if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long microseconds) && microseconds >= 0)
                {
                    OutTime = TimeSpan.FromMicroseconds(microseconds);
                    HasOutTime = true;
                }

                return null;

            case "total_size":
                if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long size))
                {
                    TotalSize = size;
                }

                return null;

            case "speed":
                if (double.TryParse(value.TrimEnd('x'), NumberStyles.Float, CultureInfo.InvariantCulture, out double speed))
                {
                    Speed = speed;
                }

                return null;

            case "progress":
                return new VmafProgress(Frame, _fps, string.Equals(value, "end", StringComparison.Ordinal));

            default:
                return null;
        }
    }
}
