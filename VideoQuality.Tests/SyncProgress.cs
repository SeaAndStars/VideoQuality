namespace VideoQuality.Tests;

/// <summary>Reports synchronously (Progress&lt;T&gt; posts to the thread pool).</summary>
public sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
