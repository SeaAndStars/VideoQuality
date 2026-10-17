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

            case "progress":
                return new VmafProgress(Frame, _fps, string.Equals(value, "end", StringComparison.Ordinal));

            default:
                return null;
        }
    }
}
