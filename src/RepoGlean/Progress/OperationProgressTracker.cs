namespace RepoGlean.Progress;

internal sealed class OperationProgressTracker : IOperationProgress
{
    private readonly IOperationProgress inner;
    private readonly object sync = new();
    private int completedRepositoryCount;
    private int repositoryTotal;
    private long candidateCount;
    private long estimatedBytes;
    private long warningCount;

    public OperationProgressTracker(IOperationProgress inner)
    {
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public void Report(OperationProgressEvent progressEvent)
    {
        ArgumentNullException.ThrowIfNull(progressEvent);
        if (progressEvent.Operation is ProgressOperation.Scan or ProgressOperation.Plan)
        {
            lock (sync)
            {
                TrackReadOnly(progressEvent);
            }
        }

        inner.Report(progressEvent);
    }

    public OperationProgressEvent CreateReadOnlyInterruptedEvent(
        ProgressOperation operation)
    {
        lock (sync)
        {
            return new OperationProgressEvent(
                ProgressEventKind.Interrupted,
                operation,
                Current: completedRepositoryCount,
                Total: repositoryTotal,
                RepositoryCount: completedRepositoryCount,
                CandidateCount: candidateCount,
                EstimatedBytes: estimatedBytes,
                WarningCount: warningCount);
        }
    }

    public void Pause() => inner.Pause();

    public void Resume() => inner.Resume();

    public ValueTask DisposeAsync() => inner.DisposeAsync();

    private void TrackReadOnly(OperationProgressEvent progressEvent)
    {
        switch (progressEvent.Kind)
        {
            case ProgressEventKind.DiscoveryStarted:
                completedRepositoryCount = 0;
                repositoryTotal = 0;
                candidateCount = 0;
                estimatedBytes = 0;
                warningCount = 0;
                break;
            case ProgressEventKind.RepositoryScanStarted:
                repositoryTotal = Math.Max(repositoryTotal, progressEvent.Total);
                break;
            case ProgressEventKind.RepositoryScanCompleted:
                completedRepositoryCount = Math.Max(
                    completedRepositoryCount,
                    progressEvent.Current);
                repositoryTotal = Math.Max(repositoryTotal, progressEvent.Total);
                candidateCount = Math.Max(candidateCount, progressEvent.CandidateCount);
                estimatedBytes = Math.Max(estimatedBytes, progressEvent.EstimatedBytes);
                break;
            case ProgressEventKind.Warning:
                warningCount = warningCount == long.MaxValue ? long.MaxValue : warningCount + 1;
                break;
        }
    }
}
