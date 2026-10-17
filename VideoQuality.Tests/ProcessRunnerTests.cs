using VideoQuality.Core.Tools;

namespace VideoQuality.Tests;

public class ProcessRunnerTests
{
    private static readonly SystemProcessRunner Runner = SystemProcessRunner.Instance;

    [Fact]
    public async Task Captures_output_error_and_exit_code()
    {
        using FakeTool tool = FakeTool.Create("tool", new FakeRule { Stdout = "out\nmore\n", Stderr = "err\n", Exit = 3 });

        ProcessResult result = await Runner.RunAsync(new ProcessRequest { FileName = tool.Path, Arguments = ["a b", "ü"] }, TestContext.Current.CancellationToken);

        Assert.Equal(3, result.ExitCode);
        Assert.Equal("out\nmore\n", result.Output);
        Assert.Equal("err\n", result.Error);
        Assert.Equal(["a b", "ü"], tool.Calls.Single());
    }

    [Fact]
    public async Task Streams_lines_and_reports_the_running_process()
    {
        using FakeTool tool = FakeTool.Create("tool", new FakeRule { StdoutLines = ["1", "2", "3"], Stderr = "e1\ne2\n" });
        var output = new List<string>();
        var errors = new List<string>();
        IProcessControl? control = null;

        await Runner.RunAsync(
            new ProcessRequest
            {
                FileName = tool.Path,
                WorkingDirectory = tool.Directory,
                OnOutputLine = line => { lock (output) { output.Add(line); } },
                OnErrorLine = line => { lock (errors) { errors.Add(line); } },
                OnStarted = c => control = c,
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(["1", "2", "3"], output);
        Assert.Equal(["e1", "e2"], errors);
        Assert.NotNull(control);
        Assert.True(control.Id > 0);
        Assert.True(control.ProcessorTime >= TimeSpan.Zero);

        // After exit: pausing is a no-op, killing is harmless, the CPU time is the last known value.
        control.Suspend();
        control.Resume();
        control.Kill();
        Assert.Equal(control.ProcessorTime, control.ProcessorTime);
    }

    [Fact]
    public async Task Keeps_only_the_tail_of_long_output()
    {
        string[] lines = Enumerable.Range(0, 400).Select(i => $"line {i:000}").ToArray();
        using FakeTool tool = FakeTool.Create("tool", new FakeRule { StdoutLines = lines });

        ProcessResult result = await Runner.RunAsync(new ProcessRequest { FileName = tool.Path, CaptureLimit = 100 }, TestContext.Current.CancellationToken);

        Assert.Equal(100, result.Output.Length);
        Assert.EndsWith("line 399\n", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancelling_kills_the_process()
    {
        using FakeTool tool = FakeTool.Create("tool", new FakeRule { Stdout = "started\n", Hang = true });
        using var cts = new CancellationTokenSource();

        Task<ProcessResult> run = Runner.RunAsync(
            new ProcessRequest { FileName = tool.Path, OnOutputLine = _ => cts.Cancel() },
            cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task Suspend_and_resume_a_running_process()
    {
        using FakeTool tool = FakeTool.Create("tool", new FakeRule { StdoutLines = ["a", "b"], LineDelayMs = 300 });
        var paused = new TaskCompletionSource<IProcessControl>();
        var lines = new List<DateTime>();

        Task<ProcessResult> run = Runner.RunAsync(
            new ProcessRequest
            {
                FileName = tool.Path,
                OnStarted = control => paused.TrySetResult(control),
                OnOutputLine = _ => { lock (lines) { lines.Add(DateTime.UtcNow); } },
            },
            TestContext.Current.CancellationToken);

        IProcessControl control = await paused.Task;
        control.Suspend();
        await Task.Delay(700, TestContext.Current.CancellationToken);
        control.Resume();
        ProcessResult result = await run;

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(2, lines.Count);
    }

    [Fact]
    public async Task A_missing_program_throws_ProcessStartException()
    {
        string missing = Path.Combine(Path.GetTempPath(), "vq-no-such-tool-" + Guid.NewGuid().ToString("N"));

        ProcessStartException ex = await Assert.ThrowsAsync<ProcessStartException>(
            () => Runner.RunAsync(new ProcessRequest { FileName = missing }, TestContext.Current.CancellationToken));

        Assert.Equal(missing, ex.FileName);
        Assert.Contains(missing, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Failed_signals_throw()
    {
        SystemProcessRunner.ThrowIfFailed(0, 42);
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => SystemProcessRunner.ThrowIfFailed(-3, 42));
        Assert.Contains("42", ex.Message, StringComparison.Ordinal);
    }
}

public class ProcessControlTests
{
    [Fact]
    public void Cpu_time_of_a_process_that_exited_unnoticed_keeps_the_last_value()
    {
        using FakeTool tool = FakeTool.Create("tool", new FakeRule());
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(tool.Path) { UseShellExecute = false })!;
        var control = new SystemProcessRunner.Control(process);
        process.WaitForExit();

        // The runner has not called Exited() yet; reading must not throw.
        TimeSpan first = control.ProcessorTime;
        Assert.True(first >= TimeSpan.Zero);
        control.Exited();
        Assert.Equal(control.ProcessorTime, control.ProcessorTime);
    }
}
