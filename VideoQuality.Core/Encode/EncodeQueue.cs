using VideoQuality.Core.Diagnostics;
using VideoQuality.Core.Localization;
using VideoQuality.Core.Scoring;
using VideoQuality.Core.Tools;

namespace VideoQuality.Core.Encode;

public enum EncodeJobState
{
    Queued,
    Running,
    Paused,
    Done,
    Failed,
    Cancelled,
}

/// <summary>One encode in the queue. Mutated by the queue; read it from <see cref="EncodeQueue.JobChanged"/>.</summary>
public sealed class EncodeJob
{
    private readonly object _gate = new();
    private readonly LinkedList<string> _log = new();

    public EncodeJob(string source, EncodeCommand command, string? name = null)
    {
        Source = source;
        Command = command;
        Name = name ?? Path.GetFileName(source);
    }

    public Guid Id { get; } = Guid.NewGuid();

    public string Name { get; }

    public string Source { get; }

    public EncodeCommand Command { get; }

    public string Output => Command.Output;

    public EncodeJobState State { get; internal set; }

    /// <summary>0 to 1 over all passes.</summary>
    public double Progress { get; internal set; }

    /// <summary>1-based pass being run.</summary>
    public int Pass { get; internal set; }

    public double Fps { get; internal set; }

    public double Speed { get; internal set; }

    public TimeSpan? Remaining { get; internal set; }

    /// <summary>Projected final size from the bytes written so far (last pass only).</summary>
    public long EstimatedSize { get; internal set; }

    public long WrittenSize { get; internal set; }

    public TimeSpan Elapsed { get; internal set; }

    public string? Error { get; internal set; }

    /// <summary>CPU time used by the running ffmpeg (for the monitor).</summary>
    public TimeSpan ProcessorTime => Control?.ProcessorTime ?? TimeSpan.Zero;

    internal IProcessControl? Control { get; set; }

    internal CancellationTokenSource Cancellation { get; set; } = new();

    /// <summary>The last lines ffmpeg printed (stderr), newest last.</summary>
    public IReadOnlyList<string> Log
    {
        get
        {
            lock (_gate)
            {
                return [.. _log];
            }
        }
    }

    internal void AddLog(string line)
    {
        lock (_gate)
        {
            _log.AddLast(line);
            if (_log.Count > 200)
            {
                _log.RemoveFirst();
            }
        }
    }

    internal void ClearLog()
    {
        lock (_gate)
        {
            _log.Clear();
        }
    }
}

/// <summary>
/// Runs encodes with the encoder ffmpeg, <see cref="Concurrency"/> at a time, in the order they were
/// added. Jobs can be paused (the process is frozen), resumed, stopped and retried.
/// </summary>
public sealed class EncodeQueue(IProcessRunner runner, Func<string> ffmpegPath)
{
    private readonly object _gate = new();
    private readonly List<EncodeJob> _jobs = [];
    private int _running;

    /// <summary>Raised (on a pool thread) whenever a job's state or progress changes.</summary>
    public event Action<EncodeJob>? JobChanged;

    /// <summary>How many encodes run at once (x265 already uses every core; 1 is usually right).</summary>
    public int Concurrency { get; set; } = 1;

    public IReadOnlyList<EncodeJob> Jobs
    {
        get
        {
            lock (_gate)
            {
                return [.. _jobs];
            }
        }
    }

    public void Add(EncodeJob job)
    {
        lock (_gate)
        {
            _jobs.Add(job);
        }

        Changed(job);
        Pump();
    }

    /// <summary>Removes a job that is not running (stop it first).</summary>
    public bool Remove(EncodeJob job)
    {
        lock (_gate)
        {
            if (job.State is EncodeJobState.Running or EncodeJobState.Paused)
            {
                return false;
            }

            return _jobs.Remove(job);
        }
    }

    public void Pause(EncodeJob job)
    {
        if (job is { State: EncodeJobState.Running, Control: { } control })
        {
            control.Suspend();
            job.State = EncodeJobState.Paused;
            Changed(job);
        }
    }

    public void Resume(EncodeJob job)
    {
        if (job is { State: EncodeJobState.Paused, Control: { } control })
        {
            control.Resume();
            job.State = EncodeJobState.Running;
            Changed(job);
        }
    }

    /// <summary>Stops a running or paused job (its partial output is deleted) or drops a queued one.</summary>
    public void Stop(EncodeJob job)
    {
        switch (job.State)
        {
            case EncodeJobState.Queued:
                job.State = EncodeJobState.Cancelled;
                Changed(job);
                break;
            case EncodeJobState.Running or EncodeJobState.Paused:
                job.Control?.Resume(); // a frozen process cannot be killed cleanly on every platform
                job.Cancellation.Cancel();
                break;
        }
    }

    /// <summary>Queues a failed or cancelled job again.</summary>
    public void Retry(EncodeJob job)
    {
        if (job.State is EncodeJobState.Failed or EncodeJobState.Cancelled)
        {
            job.State = EncodeJobState.Queued;
            job.Progress = 0;
            job.Error = null;
            job.ClearLog();
            Changed(job);
            Pump();
        }
    }

    /// <summary>Completes when nothing is queued or running.</summary>
    public async Task WhenIdleAsync(CancellationToken cancellationToken = default)
    {
        while (Jobs.Any(j => j.State is EncodeJobState.Queued or EncodeJobState.Running or EncodeJobState.Paused))
        {
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
    }

    private void Pump()
    {
        while (true)
        {
            EncodeJob? next;
            lock (_gate)
            {
                if (_running >= Math.Max(1, Concurrency))
                {
                    return;
                }

                next = _jobs.FirstOrDefault(j => j.State == EncodeJobState.Queued);
                if (next is null)
                {
                    return;
                }

                next.State = EncodeJobState.Running;
                next.Cancellation = new CancellationTokenSource();
                _running++;
            }

            EncodeJob job = next;
            _ = Task.Run(async () =>
            {
                await RunAsync(job).ConfigureAwait(false);
                lock (_gate)
                {
                    _running--;
                }

                Pump();
            });
        }
    }

    private async Task RunAsync(EncodeJob job)
    {
        CancellationTokenSource cancellation = job.Cancellation;
        var started = DateTime.UtcNow;
        int passes = job.Command.Passes.Count;
        double duration = Math.Max(job.Command.Duration.TotalSeconds, 0.001);
        string executable = ffmpegPath();
        VqLog.Info("encode", $"start {job.Name} -> {job.Output} ({passes} pass(es))");
        Changed(job);
        try
        {
            for (int pass = 0; pass < passes; pass++)
            {
                job.Pass = pass + 1;
                var parser = new FfmpegProgressParser();
                bool last = pass == passes - 1;
                IReadOnlyList<string> arguments = job.Command.Passes[pass];
                VqLog.Info("encode", FfmpegCommandBuilder.FormatCommandLine(executable, arguments));
                ProcessResult result = await runner.RunAsync(
                    new ProcessRequest
                    {
                        FileName = executable,
                        Arguments = arguments,
                        OnStarted = control => job.Control = control,
                        OnErrorLine = job.AddLog,
                        OnOutputLine = line =>
                        {
                            if (parser.Consume(line) is null)
                            {
                                return;
                            }

                            double inPass = Math.Clamp(parser.OutTime.TotalSeconds / duration, 0, 1);
                            job.Progress = (pass + inPass) / passes;
                            job.Fps = parser.Fps;
                            job.Speed = parser.Speed;
                            job.Elapsed = DateTime.UtcNow - started;
                            double remainingMedia = (passes - pass - inPass) * duration;
                            job.Remaining = parser.Speed > 0 ? TimeSpan.FromSeconds(remainingMedia / parser.Speed) : null;
                            if (last)
                            {
                                job.WrittenSize = parser.TotalSize;
                                job.EstimatedSize = inPass > 0.02 ? (long)(parser.TotalSize / inPass) : 0;
                            }

                            Changed(job);
                        },
                    },
                    cancellation.Token).ConfigureAwait(false);

                job.Control = null;
                if (result.ExitCode != 0)
                {
                    throw new InvalidOperationException(Loc.T("encode.error.exit", result.ExitCode, Tail(job.Log)));
                }
            }

            job.Progress = 1;
            job.Remaining = TimeSpan.Zero;
            job.WrittenSize = File.Exists(job.Output) ? new FileInfo(job.Output).Length : 0;
            job.EstimatedSize = job.WrittenSize;
            job.State = EncodeJobState.Done;
            VqLog.Info("encode", $"done {job.Name} {job.WrittenSize} bytes");
        }
        catch (OperationCanceledException)
        {
            job.State = EncodeJobState.Cancelled;
            FileSystem.TryDelete(job.Output);
        }
        catch (Exception ex)
        {
            job.State = EncodeJobState.Failed;
            job.Error = ex.Message;
            VqLog.Warn("encode", $"failed {job.Name}", ex);
            FileSystem.TryDelete(job.Output);
        }
        finally
        {
            job.Control = null;
            job.Elapsed = DateTime.UtcNow - started;
            foreach (string prefix in job.Command.TemporaryFiles)
            {
                DeleteWithPrefix(prefix);
            }
        }

        Changed(job);
    }

    private void Changed(EncodeJob job) => JobChanged?.Invoke(job);

    private static string Tail(IReadOnlyList<string> log) =>
        string.Join(Environment.NewLine, log.Where(l => !l.StartsWith("frame=", StringComparison.Ordinal)).TakeLast(8));

    /// <summary>Two-pass logs: <c>prefix-0.log</c>, <c>prefix-0.log.mbtree</c>, <c>prefix.cutree</c>, ….</summary>
    private static void DeleteWithPrefix(string prefix)
    {
        string directory = Path.GetDirectoryName(prefix)!;
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (string file in Directory.EnumerateFiles(directory, Path.GetFileName(prefix) + "*"))
        {
            FileSystem.TryDelete(file);
        }
    }
}
