namespace RepoGlean.Progress;

internal enum ProgressMode
{
    None,
    Interactive,
    Verbose,
}

internal enum ProgressOperation
{
    Scan,
    Clean,
}

internal enum ProgressEventKind
{
    DiscoveryStarted,
    RepositoryFound,
    DiscoveryCompleted,
    RepositoryScanStarted,
    RepositoryScanCompleted,
    CandidateStarted,
    CandidateCompleted,
    Warning,
    Completed,
    Interrupted,
    Failed,
}

internal enum ProgressCandidateOutcome
{
    Deleted,
    Validated,
    Skipped,
    Failed,
}

internal sealed record OperationProgressEvent(
    ProgressEventKind Kind,
    ProgressOperation Operation,
    IReadOnlyList<string>? Roots = null,
    string? Path = null,
    string? Message = null,
    int Current = 0,
    int Total = 0,
    long RepositoryCount = 0,
    long CurrentCandidateCount = 0,
    long CandidateCount = 0,
    long CurrentEstimatedBytes = 0,
    long EstimatedBytes = 0,
    long DeletedCount = 0,
    long ValidatedCount = 0,
    long SkippedCount = 0,
    long FailedCount = 0,
    long WarningCount = 0,
    bool DryRun = false,
    ProgressCandidateOutcome? Outcome = null);
