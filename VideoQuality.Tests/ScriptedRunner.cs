using System.Text.RegularExpressions;
using VideoQuality.Core.Tools;

namespace VideoQuality.Tests;

/// <summary>An in-memory process runner: each request is answered by the handler (no process starts).</summary>
public sealed class ScriptedRunner(Func<ProcessRequest, CancellationToken, Task<ProcessResult>> handler) : IProcessRunner
{
    private readonly List<ProcessRequest> _requests = [];

    public ScriptedRunner(Func<ProcessRequest, ProcessResult> handler)
        : this((request, _) => Task.FromResult(handler(request)))
    {
    }

    public IReadOnlyList<ProcessRequest> Requests
    {
        get
        {
            lock (_requests)
            {
                return [.. _requests];
            }
        }
    }

    public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
    {
        lock (_requests)
        {
            _requests.Add(request);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return handler(request, cancellationToken);
    }

    public static ProcessResult Ok(string output = "", string error = "") => new(0, output, error);

    /// <summary>Feeds lines to the request's output callback, as a running process would.</summary>
    public static void Emit(ProcessRequest request, params string[] lines)
    {
        foreach (string line in lines)
        {
            request.OnOutputLine?.Invoke(line);
        }
    }

    /// <summary>The (unescaped) log_path of a libvmaf filter in the arguments.</summary>
    public static string LogPath(ProcessRequest request)
    {
        string filter = request.Arguments.Single(a => a.Contains("log_path=", StringComparison.Ordinal));
        string raw = Regex.Match(filter, @"log_path=(.+?):log_fmt").Groups[1].Value;
        // Two levels of filtergraph escaping.
        return Regex.Replace(Regex.Replace(raw, @"\\(.)", "$1"), @"\\(.)", "$1");
    }
}
