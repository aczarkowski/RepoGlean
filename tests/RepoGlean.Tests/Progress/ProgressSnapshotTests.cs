using RepoGlean.Progress;

namespace RepoGlean.Tests.Progress;

public sealed class ProgressSnapshotTests
{
    [Fact]
    public void Apply_formats_discovery_from_the_latest_authoritative_repository_count()
    {
        var snapshot = new ProgressSnapshot()
            .Apply(new OperationProgressEvent(
                ProgressEventKind.DiscoveryStarted,
                ProgressOperation.Scan))
            .Apply(new OperationProgressEvent(
                ProgressEventKind.RepositoryFound,
                ProgressOperation.Scan,
                Path: "/work/my-api",
                RepositoryCount: 12));

        Assert.Equal(
            "Discovering repositories • 12 found",
            snapshot.Format());
    }

    [Fact]
    public void Apply_formats_repository_scan_from_cumulative_result_totals()
    {
        var snapshot = new ProgressSnapshot()
            .Apply(new OperationProgressEvent(
                ProgressEventKind.RepositoryScanStarted,
                ProgressOperation.Scan,
                Path: "/work/my-api",
                Current: 7,
                Total: 18))
            .Apply(new OperationProgressEvent(
                ProgressEventKind.RepositoryScanCompleted,
                ProgressOperation.Scan,
                Path: "/work/my-api",
                Current: 7,
                Total: 18,
                CandidateCount: 23,
                EstimatedBytes: 1503238553));

        Assert.Equal(
            "Scanning repositories 7/18 • 23 candidates • 1.4 GiB estimated",
            snapshot.Format());
    }

    [Fact]
    public void Apply_formats_permanent_cleanup_and_advances_bytes_only_for_deleted_outcomes()
    {
        var snapshot = new ProgressSnapshot()
            .Apply(new OperationProgressEvent(
                ProgressEventKind.CandidateStarted,
                ProgressOperation.Clean,
                Path: "/work/my-api/obj",
                Current: 1,
                Total: 3))
            .Apply(new OperationProgressEvent(
                ProgressEventKind.CandidateCompleted,
                ProgressOperation.Clean,
                Path: "/work/my-api/obj",
                Current: 1,
                Total: 3,
                DeletedCount: 1,
                EstimatedBytes: 650117120,
                Outcome: ProgressCandidateOutcome.Deleted))
            .Apply(new OperationProgressEvent(
                ProgressEventKind.CandidateCompleted,
                ProgressOperation.Clean,
                Path: "/work/my-api/cache",
                Current: 2,
                Total: 3,
                DeletedCount: 1,
                SkippedCount: 1,
                EstimatedBytes: 987654321,
                Outcome: ProgressCandidateOutcome.Skipped));

        Assert.Equal(
            "Cleaning artifacts 2/3 • 1 deleted • 620 MiB estimated",
            snapshot.Format());
    }

    [Fact]
    public void Apply_formats_dry_run_with_validated_outcomes()
    {
        var snapshot = new ProgressSnapshot()
            .Apply(new OperationProgressEvent(
                ProgressEventKind.CandidateStarted,
                ProgressOperation.Clean,
                Path: "/work/my-api/obj",
                Current: 1,
                Total: 2,
                DryRun: true))
            .Apply(new OperationProgressEvent(
                ProgressEventKind.CandidateCompleted,
                ProgressOperation.Clean,
                Path: "/work/my-api/obj",
                Current: 1,
                Total: 2,
                ValidatedCount: 1,
                EstimatedBytes: 650117120,
                DryRun: true,
                Outcome: ProgressCandidateOutcome.Validated));

        Assert.Equal(
            "Validating artifacts 1/2 • 1 validated • 620 MiB estimated",
            snapshot.Format());
    }

    [Fact]
    public void Apply_warning_increments_once_without_replacing_the_current_stage()
    {
        var snapshot = new ProgressSnapshot()
            .Apply(new OperationProgressEvent(
                ProgressEventKind.RepositoryScanStarted,
                ProgressOperation.Scan,
                Path: "/work/my-api",
                Current: 7,
                Total: 18,
                CandidateCount: 23,
                EstimatedBytes: 1503238553))
            .Apply(new OperationProgressEvent(
                ProgressEventKind.Warning,
                ProgressOperation.Scan,
                Path: "/work/unreadable",
                WarningCount: 99));

        Assert.Equal(ProgressEventKind.RepositoryScanStarted, snapshot.Phase);
        Assert.Equal("/work/my-api", snapshot.OptionalPath);
        Assert.Equal(1, snapshot.WarningCount);
        Assert.Equal(
            "Scanning repositories 7/18 • 23 candidates • 1.4 GiB estimated • 1 warnings",
            snapshot.Format());
    }
}
