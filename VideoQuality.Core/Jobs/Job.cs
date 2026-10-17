using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace VideoQuality.Core.Jobs;

public enum JobKind
{
    Index,
    Score,
    Export,
}

public enum JobState
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled,
}

/// <summary>Progress reported by a job. <see cref="Fraction"/> is null when indeterminate.</summary>
public sealed record JobProgress(double? Fraction, string? Detail);

/// <summary>
/// One unit of long-running work (a score, an index build, an export). The work factory receives the
/// job, a progress sink and a cancellation token, and is expected to report through the sink rather
/// than touching UI state.
/// </summary>
public sealed class Job : INotifyPropertyChanged
{
    public Guid Id { get; } = Guid.NewGuid();

    public required string Title { get; init; }

    public required JobKind Kind { get; init; }

    /// <summary>Optional payload, for example the encode being scored.</summary>
    public object? Subject { get; init; }

    public required Func<Job, IProgress<JobProgress>, CancellationToken, Task> Work { get; init; }

    public CancellationTokenSource Cancellation { get; } = new();

    /// <summary>Completes when the job reaches a terminal state (never faults).</summary>
    public Task Completion { get; internal set; } = Task.CompletedTask;

    private JobState _state = JobState.Queued;
    public JobState State
    {
        get => _state;
        internal set => Set(ref _state, value);
    }

    private double? _progress;
    public double? Progress
    {
        get => _progress;
        internal set => Set(ref _progress, value);
    }

    private string? _detail;
    public string? Detail
    {
        get => _detail;
        internal set => Set(ref _detail, value);
    }

    private string? _error;
    public string? Error
    {
        get => _error;
        internal set => Set(ref _error, value);
    }

    private TimeSpan _elapsed;
    public TimeSpan Elapsed
    {
        get => _elapsed;
        internal set => Set(ref _elapsed, value);
    }

    public bool IsFinished => State is JobState.Succeeded or JobState.Failed or JobState.Cancelled;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
