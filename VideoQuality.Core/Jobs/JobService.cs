using System.Collections.ObjectModel;
using System.Diagnostics;
using VideoQuality.Core.Diagnostics;

namespace VideoQuality.Core.Jobs;

/// <summary>
/// Runs jobs with per-kind lanes so expensive work does not overlap. Scoring is serialised (one
/// ffmpeg/GPU run at a time); indexing can run alongside a score; exports are serialised.
/// <para>
/// Jobs are created and enqueued from the UI thread; their observable state is updated from the
/// thread pool, so consumers must marshal to their UI thread (Avalonia bindings do this).
/// </para>
/// </summary>
public sealed class JobService
{
    private readonly Dictionary<JobKind, SemaphoreSlim> _lanes = new()
    {
        [JobKind.Score] = new SemaphoreSlim(1, 1),
        [JobKind.Index] = new SemaphoreSlim(2, 2),
        [JobKind.Export] = new SemaphoreSlim(1, 1),
    };

    public ObservableCollection<Job> Jobs { get; } = [];

    /// <summary>Raised when a job reaches a terminal state.</summary>
    public event EventHandler<Job>? Completed;

    public Job Enqueue(Job job)
    {
        Jobs.Add(job);
        job.Completion = RunAsync(job);
        return job;
    }

    public void Cancel(Job job)
    {
        if (!job.IsFinished)
        {
            job.Cancellation.Cancel();
        }
    }

    /// <summary>Creates a fresh job with the same work factory.</summary>
    public Job Retry(Job job) => Enqueue(new Job
    {
        Title = job.Title,
        Kind = job.Kind,
        Subject = job.Subject,
        Work = job.Work,
    });

    private async Task RunAsync(Job job)
    {
        SemaphoreSlim lane = _lanes[job.Kind];
        bool held = false;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            await lane.WaitAsync(job.Cancellation.Token).ConfigureAwait(false);
            held = true;

            job.State = JobState.Running;
            VqLog.Info("job", $"{job.Id:N} start {job.Kind} '{job.Title}'");

            var progress = new Progress<JobProgress>(value =>
            {
                job.Progress = value.Fraction;
                job.Detail = value.Detail;
            });

            await job.Work(job, progress, job.Cancellation.Token).ConfigureAwait(false);
            job.State = JobState.Succeeded;
        }
        catch (OperationCanceledException)
        {
            job.State = JobState.Cancelled;
        }
        catch (Exception ex)
        {
            job.Error = ex.Message;
            job.State = JobState.Failed;
            VqLog.Error("job", $"{job.Id:N} '{job.Title}' failed", ex);
        }
        finally
        {
            stopwatch.Stop();
            job.Elapsed = stopwatch.Elapsed;

            if (held)
            {
                lane.Release();
            }

            VqLog.Info("job", $"{job.Id:N} {job.State} '{job.Title}' in {job.Elapsed.TotalSeconds:F2}s");
            Completed?.Invoke(this, job);
        }
    }
}
