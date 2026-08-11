using RepoGlean.Progress;
using RepoGlean.Tests.Support;

namespace RepoGlean.Tests.Progress;

public sealed class OperationProgressTrackerTests
{
    [Fact]
    public Task Scan_interruption_uses_completed_scan_totals_without_regressing_to_discovery_or_start_values() =>
        AssertReadOnlyInterruptionAsync(ProgressOperation.Scan);

    [Fact]
    public Task Plan_interruption_uses_completed_scan_totals_without_regressing_to_discovery_or_start_values() =>
        AssertReadOnlyInterruptionAsync(ProgressOperation.Plan);

    [Fact]
    public async Task Audit_interruption_uses_completed_finding_totals()
    {
        var inner = new RecordingProgress();
        await using var tracker = new OperationProgressTracker(inner);
        tracker.Report(new OperationProgressEvent(
            ProgressEventKind.DiscoveryStarted,
            ProgressOperation.Audit,
            Roots: ["/work"]));
        tracker.Report(new OperationProgressEvent(
            ProgressEventKind.RepositoryScanStarted,
            ProgressOperation.Audit,
            Path: "/work/first",
            Current: 1,
            Total: 2));
        tracker.Report(new OperationProgressEvent(
            ProgressEventKind.RepositoryScanCompleted,
            ProgressOperation.Audit,
            Path: "/work/first",
            Current: 1,
            Total: 2,
            RepositoryCount: 1,
            FindingCount: 2,
            EstimatedBytes: 18));
        tracker.Report(new OperationProgressEvent(
            ProgressEventKind.Warning,
            ProgressOperation.Audit,
            Path: "/work/second",
            Message: "Second repository interrupted."));

        var interrupted = tracker.CreateReadOnlyInterruptedEvent(ProgressOperation.Audit);

        Assert.Equal(1, interrupted.RepositoryCount);
        Assert.Equal(2, interrupted.FindingCount);
        Assert.Equal(18, interrupted.EstimatedBytes);
        Assert.Equal(1, interrupted.WarningCount);
    }

    private static async Task AssertReadOnlyInterruptionAsync(
        ProgressOperation operation)
    {
        var inner = new RecordingProgress();
        await using var tracker = new OperationProgressTracker(inner);
        var events = new[]
        {
            new OperationProgressEvent(
                ProgressEventKind.DiscoveryStarted,
                operation,
                Roots: ["/work"]),
            new OperationProgressEvent(
                ProgressEventKind.RepositoryFound,
                operation,
                Path: "/work/first",
                RepositoryCount: 1),
            new OperationProgressEvent(
                ProgressEventKind.RepositoryFound,
                operation,
                Path: "/work/second",
                RepositoryCount: 2),
            new OperationProgressEvent(
                ProgressEventKind.Warning,
                operation,
                Path: "/work/unreadable",
                Message: "Unable to inspect path."),
            new OperationProgressEvent(
                ProgressEventKind.DiscoveryCompleted,
                operation,
                RepositoryCount: 2),
            new OperationProgressEvent(
                ProgressEventKind.RepositoryScanCompleted,
                operation,
                Path: "/work/first",
                Current: 1,
                Total: 2,
                CandidateCount: 3,
                EstimatedBytes: 50),
            new OperationProgressEvent(
                ProgressEventKind.RepositoryScanStarted,
                operation,
                Path: "/work/second",
                Current: 2,
                Total: 2,
                CandidateCount: 0,
                EstimatedBytes: 0),
            new OperationProgressEvent(
                ProgressEventKind.Warning,
                operation,
                Path: "/work/second",
                Message: "Second repository interrupted."),
        };

        foreach (var progressEvent in events)
        {
            tracker.Report(progressEvent);
        }

        var interrupted = tracker.CreateReadOnlyInterruptedEvent(operation);

        Assert.Equal(events, inner.Events);
        Assert.Equal(ProgressEventKind.Interrupted, interrupted.Kind);
        Assert.Equal(operation, interrupted.Operation);
        Assert.Equal(1, interrupted.Current);
        Assert.Equal(2, interrupted.Total);
        Assert.Equal(1, interrupted.RepositoryCount);
        Assert.Equal(3, interrupted.CandidateCount);
        Assert.Equal(50, interrupted.EstimatedBytes);
        Assert.Equal(2, interrupted.WarningCount);
    }
}
