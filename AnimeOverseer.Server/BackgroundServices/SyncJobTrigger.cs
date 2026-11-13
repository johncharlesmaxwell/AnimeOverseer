using System.Collections.Concurrent;

namespace AnimeOverseer.Server.BackgroundServices;

/// <summary>
/// Coalesces requests for the background runner to check its queue immediately.
/// A single pending signal is sufficient because the runner drains queued jobs
/// one at a time.
/// </summary>
public sealed class SyncJobTrigger
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    public void Signal()
    {
        try { _signal.Release(); }
        catch (SemaphoreFullException) { }
    }

    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
        => _signal.WaitAsync(timeout, cancellationToken);

    private readonly ConcurrentDictionary<int, Action> _runningJobs = new();

    /// <summary>
    /// Registers the running job's cancel action, so a cancel request reaches it at once
    /// instead of the runner polling the database. Dispose the result when the job ends.
    /// </summary>
    public IDisposable RegisterRunningJob(int jobId, Action cancel)
    {
        _runningJobs[jobId] = cancel;
        return new Registration(() => _runningJobs.TryRemove(new KeyValuePair<int, Action>(jobId, cancel)));
    }

    /// <summary>Cancels the job if it is running in this process; returns whether it was.</summary>
    public bool RequestCancellation(int jobId)
    {
        if (!_runningJobs.TryGetValue(jobId, out var cancel)) return false;
        cancel();
        return true;
    }

    private sealed class Registration(Action dispose) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) dispose();
        }
    }
}
