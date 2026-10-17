using VideoQuality.Core.Jobs;

namespace VideoQuality.Tests;

public class JobServiceTests
{
    private static Job NewJob(
        string title = "test",
        JobKind kind = JobKind.Score,
        Func<Job, IProgress<JobProgress>, CancellationToken, Task>? work = null,
        object? subject = null) => new()
    {
        Title = title,
        Kind = kind,
        Subject = subject,
        Work = work ?? ((_, _, _) => Task.CompletedTask),
    };

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("condition was not met in time");
            }

            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task Runs_to_success()
    {
        var service = new JobService();
        Job job = service.Enqueue(NewJob());

        await job.Completion;

        Assert.Equal(JobState.Succeeded, job.State);
        Assert.Null(job.Error);
        Assert.Contains(job, service.Jobs);
    }

    [Fact]
    public async Task Failure_records_the_error()
    {
        var service = new JobService();
        Job job = service.Enqueue(NewJob(work: (_, _, _) =>
            Task.FromException(new InvalidOperationException("boom"))));

        await job.Completion;

        Assert.Equal(JobState.Failed, job.State);
        Assert.Equal("boom", job.Error);
    }

    [Fact]
    public async Task Cancelling_a_running_job_marks_it_cancelled()
    {
        var service = new JobService();
        var started = new TaskCompletionSource();

        Job job = service.Enqueue(NewJob(work: async (_, _, token) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, token);
        }));

        await started.Task;
        service.Cancel(job);
        await job.Completion;

        Assert.Equal(JobState.Cancelled, job.State);
    }

    [Fact]
    public async Task Cancelling_a_queued_job_prevents_it_from_running()
    {
        var service = new JobService();
        var started = new TaskCompletionSource();
        var gate = new TaskCompletionSource();
        bool secondRan = false;

        Job first = service.Enqueue(NewJob(title: "first", work: async (_, _, token) =>
        {
            started.SetResult();
            await gate.Task.WaitAsync(token);
        }));

        await started.Task;

        Job second = service.Enqueue(NewJob(title: "second", work: (_, _, _) =>
        {
            secondRan = true;
            return Task.CompletedTask;
        }));

        service.Cancel(second);
        await second.Completion;

        Assert.Equal(JobState.Cancelled, second.State);
        Assert.False(secondRan);

        gate.SetResult();
        await first.Completion;
    }

    [Fact]
    public async Task Score_jobs_never_overlap()
    {
        var service = new JobService();
        int active = 0;
        int maxActive = 0;

        async Task Work()
        {
            int now = Interlocked.Increment(ref active);
            maxActive = Math.Max(maxActive, now);
            await Task.Delay(40);
            Interlocked.Decrement(ref active);
        }

        Job first = service.Enqueue(NewJob(title: "one", work: (_, _, _) => Work()));
        Job second = service.Enqueue(NewJob(title: "two", work: (_, _, _) => Work()));

        await Task.WhenAll(first.Completion, second.Completion);

        Assert.Equal(1, maxActive);
        Assert.Equal(JobState.Succeeded, first.State);
        Assert.Equal(JobState.Succeeded, second.State);
    }

    [Fact]
    public async Task Index_jobs_run_alongside_a_score()
    {
        var service = new JobService();
        var started = new TaskCompletionSource();
        var gate = new TaskCompletionSource();

        Job score = service.Enqueue(NewJob(title: "score", kind: JobKind.Score, work: async (_, _, token) =>
        {
            started.SetResult();
            await gate.Task.WaitAsync(token);
        }));

        await started.Task;

        Job index = service.Enqueue(NewJob(title: "index", kind: JobKind.Index));
        await index.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(JobState.Succeeded, index.State);
        Assert.Equal(JobState.Running, score.State);

        gate.SetResult();
        await score.Completion;
    }

    [Fact]
    public async Task Retry_creates_a_new_job_with_the_same_work()
    {
        var service = new JobService();
        Job failed = service.Enqueue(NewJob(work: (_, _, _) =>
            Task.FromException(new InvalidOperationException("nope"))));

        await failed.Completion;

        Job retry = service.Retry(failed);

        Assert.NotEqual(failed.Id, retry.Id);
        Assert.Equal(failed.Title, retry.Title);
        Assert.Equal(failed.Kind, retry.Kind);

        await retry.Completion;
        Assert.Equal(JobState.Failed, retry.State);
    }

    [Fact]
    public async Task Progress_is_surfaced_on_the_job()
    {
        var service = new JobService();
        var gate = new TaskCompletionSource();

        Job job = service.Enqueue(NewJob(work: async (_, progress, _) =>
        {
            progress.Report(new JobProgress(0.5, "half way"));
            await gate.Task;
        }));

        await WaitUntilAsync(() => job.Progress is 0.5 && job.Detail is "half way");
        Assert.Equal("half way", job.Detail);

        gate.SetResult();
        await job.Completion;
    }

    [Fact]
    public async Task Completion_event_fires_once_per_job()
    {
        var service = new JobService();
        int completed = 0;
        service.Completed += (_, _) => Interlocked.Increment(ref completed);

        Job job = service.Enqueue(NewJob());
        await job.Completion;

        Assert.Equal(1, completed);
    }
}
