using VideoQuality.Core.Encode;
using VideoQuality.Core.Tools;

namespace VideoQuality.Tests;

/// <summary>Queue mechanics against a scripted ffmpeg that prints -progress blocks.</summary>
public class EncodeQueueTests
{
    private static readonly string[] Progress =
    [
        "frame=24", "fps=48.0", "out_time_us=N/A", "total_size=0", "speed=N/A", "progress=continue",
        "frame=48", "fps=48.0", "out_time_us=5000000", "total_size=50000", "speed=2.5x", "progress=continue",
        "frame=96", "fps=48.0", "out_time_ms=10000000", "total_size=100000", "speed=2.0x", "progress=end",
    ];

    private static EncodeJob Job(string output, int passes = 1, IReadOnlyList<string>? temporary = null, double seconds = 10) =>
        new("/in/ep.mkv", new EncodeCommand(
            Enumerable.Range(0, passes).Select(_ => (IReadOnlyList<string>)["-i", "/in/ep.mkv", output]).ToList(),
            output, temporary ?? [], [], 0, TimeSpan.FromSeconds(seconds)));

    private static string Temp(string name) => Path.Combine(Path.GetTempPath(), $"vq-queue-{Guid.NewGuid():N}-{name}");

    [Fact]
    public async Task Progress_eta_and_size_come_from_the_progress_blocks()
    {
        using FakeTool ffmpeg = FakeTool.Create("ffmpeg", new FakeRule { StdoutLines = Progress, WriteLastArg = true, FileContent = new string('x', 1234) });
        var queue = new EncodeQueue(SystemProcessRunner.Instance, () => ffmpeg.Path);
        var snapshots = new List<(double Progress, TimeSpan? Remaining, long Estimate, double Speed)>();
        queue.JobChanged += j => { lock (snapshots) { snapshots.Add((j.Progress, j.Remaining, j.EstimatedSize, j.Speed)); } };
        string output = Temp("out.mkv");

        EncodeJob job = Job(output);
        queue.Add(job);
        await queue.WhenIdleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(EncodeJobState.Done, job.State);
        Assert.Contains(snapshots, s => s.Progress == 0.5 && s.Remaining == TimeSpan.FromSeconds(2) && s.Estimate == 100000);
        Assert.Contains(snapshots, s => s.Progress == 0 && s.Remaining is null);
        Assert.Equal(1234, job.WrittenSize);
        Assert.Equal(TimeSpan.Zero, job.Remaining);
        Assert.Equal(48, job.Fps);
        Assert.True(job.Elapsed > TimeSpan.Zero);
        Assert.Equal(TimeSpan.Zero, job.ProcessorTime);
        Assert.Single(queue.Jobs);
        Assert.True(queue.Remove(job));
        Assert.Empty(queue.Jobs);
        File.Delete(output);
    }

    [Fact]
    public async Task Failure_keeps_the_log_tail_and_retry_runs_again()
    {
        using FakeTool ffmpeg = FakeTool.Create("ffmpeg", new FakeRule { Stderr = "frame=  1 fps=0\nError opening output\nConversion failed!\n", Exit = 1 });
        var queue = new EncodeQueue(SystemProcessRunner.Instance, () => ffmpeg.Path);
        EncodeJob job = Job(Temp("fail.mkv"));

        queue.Add(job);
        await queue.WhenIdleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(EncodeJobState.Failed, job.State);
        Assert.Contains("Conversion failed!", job.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("frame=", job.Error, StringComparison.Ordinal);
        Assert.Equal(3, job.Log.Count);

        queue.Retry(job);
        await queue.WhenIdleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(EncodeJobState.Failed, job.State);
        Assert.Equal(2, ffmpeg.Calls.Count);

        queue.Retry(Job("/never")); // not failed: ignored
    }

    [Fact]
    public async Task Log_keeps_the_last_200_lines()
    {
        string many = string.Join('\n', Enumerable.Range(0, 250).Select(i => $"line {i}")) + "\n";
        using FakeTool ffmpeg = FakeTool.Create("ffmpeg", new FakeRule { Stderr = many });
        var queue = new EncodeQueue(SystemProcessRunner.Instance, () => ffmpeg.Path);
        EncodeJob job = Job(Temp("log.mkv"));

        queue.Add(job);
        await queue.WhenIdleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(200, job.Log.Count);
        Assert.Equal("line 249", job.Log[^1]);
    }

    [Fact]
    public async Task Pause_resume_and_stop_a_running_job()
    {
        using FakeTool ffmpeg = FakeTool.Create("ffmpeg", new FakeRule { StdoutLines = ["progress=continue"], Hang = true });
        var queue = new EncodeQueue(SystemProcessRunner.Instance, () => ffmpeg.Path);
        string output = Temp("stop.mkv");
        File.WriteAllText(output, "partial");
        EncodeJob job = Job(output);
        var started = new TaskCompletionSource();
        queue.JobChanged += j => { if (j.Progress == 0 && j.Fps == 0 && j.State == EncodeJobState.Running && j.ProcessorTime >= TimeSpan.Zero && j.Elapsed > TimeSpan.Zero) { started.TrySetResult(); } };

        queue.Add(job);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        queue.Pause(job);
        Assert.Equal(EncodeJobState.Paused, job.State);
        Assert.False(queue.Remove(job));
        queue.Pause(job); // already paused: ignored
        queue.Resume(job);
        Assert.Equal(EncodeJobState.Running, job.State);
        queue.Resume(job); // already running: ignored
        queue.Pause(job);
        queue.Stop(job);
        await queue.WhenIdleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(EncodeJobState.Cancelled, job.State);
        Assert.False(File.Exists(output));
        queue.Stop(job); // finished: ignored
    }

    [Fact]
    public async Task Jobs_run_in_order_one_at_a_time_and_queued_ones_can_be_dropped()
    {
        using FakeTool ffmpeg = FakeTool.Create("ffmpeg", new FakeRule { DelayMs = 200 });
        var queue = new EncodeQueue(SystemProcessRunner.Instance, () => ffmpeg.Path) { Concurrency = 1 };
        EncodeJob first = Job(Temp("1.mkv"));
        EncodeJob second = Job(Temp("2.mkv"));
        EncodeJob third = Job(Temp("3.mkv"));
        int maxRunning = 0;
        queue.JobChanged += _ =>
        {
            int running = queue.Jobs.Count(j => j.State == EncodeJobState.Running);
            maxRunning = Math.Max(maxRunning, running);
        };

        queue.Add(first);
        queue.Add(second);
        queue.Add(third);
        queue.Stop(third);
        await queue.WhenIdleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, maxRunning);
        Assert.Equal(EncodeJobState.Done, first.State);
        Assert.Equal(EncodeJobState.Done, second.State);
        Assert.Equal(EncodeJobState.Cancelled, third.State);
        Assert.Equal(2, ffmpeg.Calls.Count);
    }

    [Fact]
    public async Task Two_passes_clean_up_their_statistics_files()
    {
        using FakeTool ffmpeg = FakeTool.Create("ffmpeg", new FakeRule());
        string folder = Temp("logs");
        Directory.CreateDirectory(folder);
        string prefix = Path.Combine(folder, "vq-2pass-x");
        File.WriteAllText(prefix + "-0.log", "stats");
        File.WriteAllText(prefix + "-0.log.mbtree", "tree");
        File.WriteAllText(Path.Combine(folder, "keep.txt"), "other");
        var queue = new EncodeQueue(SystemProcessRunner.Instance, () => ffmpeg.Path);
        EncodeJob job = Job(Temp("2pass.mkv"), passes: 2, temporary: [prefix, Path.Combine(Temp("gone"), "x")]);

        queue.Add(job);
        await queue.WhenIdleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(EncodeJobState.Done, job.State);
        Assert.Equal(2, job.Pass);
        Assert.Equal(["keep.txt"], Directory.GetFiles(folder).Select(Path.GetFileName));
        Directory.Delete(folder, true);
    }

    [Fact]
    public async Task Missing_encoder_fails_the_job()
    {
        var queue = new EncodeQueue(SystemProcessRunner.Instance, () => Temp("no-ffmpeg"));
        EncodeJob job = Job(Temp("x.mkv"));

        queue.Add(job);
        await queue.WhenIdleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(EncodeJobState.Failed, job.State);
        Assert.Contains("no-ffmpeg", job.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stopping_before_the_process_starts_cancels_without_listeners()
    {
        EncodeQueue queue = null!;
        EncodeJob job = Job(Temp("early.mkv"));
        var runner = new ScriptedRunner(_ => ScriptedRunner.Ok());
        queue = new EncodeQueue(runner, () =>
        {
            queue.Stop(job); // running, but no process yet
            return "ffmpeg";
        });

        queue.Add(job);
        await queue.WhenIdleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(EncodeJobState.Cancelled, job.State);

        queue.Pause(job);
        queue.Resume(job);
        queue.Retry(job);
        await queue.WhenIdleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(EncodeJobState.Cancelled, job.State); // stopped again by the callback
    }

    [Fact]
    public async Task A_queued_job_is_dropped_without_listeners()
    {
        var release = new TaskCompletionSource<ProcessResult>();
        var queue = new EncodeQueue(new ScriptedRunner((_, _) => release.Task), () => "ffmpeg");
        EncodeJob running = Job(Temp("a.mkv"));
        EncodeJob waiting = Job(Temp("b.mkv"));
        queue.Add(running);
        queue.Add(waiting);
        queue.Stop(waiting);
        Assert.Equal(EncodeJobState.Cancelled, waiting.State);
        release.SetResult(ScriptedRunner.Ok());
        await queue.WhenIdleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(EncodeJobState.Done, running.State);
    }
}
