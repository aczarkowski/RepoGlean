using RepoGlean.Progress;
using RepoGlean.Tests.Support;

namespace RepoGlean.Tests.Progress;

public sealed class OperationProgressTrackerTests
{
    [Fact]
    public async Task Scan_interruption_uses_completed_scan_totals_without_regressing_to_discovery_or_start_values()
    {
        var inner = new RecordingProgress();
        await using var tracker = new OperationProgressTracker(inner);
        var events = new[]
        {
            new OperationProgressEvent(
                ProgressEventKind.DiscoveryStarted,
                ProgressOperation.Scan,
                Roots: ["/work"]),
            new OperationProgressEvent(
                ProgressEventKind.RepositoryFound,
                ProgressOperation.Scan,
                Path: "/work/first",
                RepositoryCount: 1),
            new OperationProgressEvent(
                ProgressEventKind.RepositoryFound,
                ProgressOperation.Scan,
                Path: "/work/second",
                RepositoryCount: 2),
            new OperationProgressEvent(
                ProgressEventKind.Warning,
                ProgressOperation.Scan,
                Path: "/work/unreadable",
                Message: "Unable to inspect path."),
            new OperationProgressEvent(
                ProgressEventKind.DiscoveryCompleted,
                ProgressOperation.Scan,
                RepositoryCount: 2),
            new OperationProgressEvent(
                ProgressEventKind.RepositoryScanCompleted,
                ProgressOperation.Scan,
                Path: "/work/first",
                Current: 1,
                Total: 2,
                CandidateCount: 3,
                EstimatedBytes: 50),
            new OperationProgressEvent(
                ProgressEventKind.RepositoryScanStarted,
                ProgressOperation.Scan,
                Path: "/work/second",
                Current: 2,
                Total: 2,
                CandidateCount: 0,
                EstimatedBytes: 0),
            new OperationProgressEvent(
                ProgressEventKind.Warning,
                ProgressOperation.Scan,
                Path: "/work/second",
                Message: "Second repository interrupted."),
        };

        foreach (var progressEvent in events) tracker.Report(progressEvent);
        var interrupted = tracker.CreateScanInterruptedEvent();

        Assert.Equal(events, inner.Events);
        Assert.Equal(ProgressEventKind.Interrupted, interrupted.Kind);
        Assert.Equal(ProgressOperation.Scan, interrupted.Operation);
        Assert.Equal(1, interrupted.Current);
        Assert.Equal(2, interrupted.Total);
        Assert.Equal(1, interrupted.RepositoryCount);
        Assert.Equal(3, interrupted.CandidateCount);
        Assert.Equal(50, interrupted.EstimatedBytes);
        Assert.Equal(2, interrupted.WarningCount);
    }
}
