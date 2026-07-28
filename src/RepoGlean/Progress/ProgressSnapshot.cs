namespace RepoGlean.Progress;

internal sealed record ProgressSnapshot(
    ProgressEventKind? Phase = null,
    string? OptionalPath = null,
    int Current = 0,
    int Total = 0,
    long RepositoryCount = 0,
    long CandidateCount = 0,
    long DeletedCount = 0,
    long ValidatedCount = 0,
    long SkippedCount = 0,
    long FailedCount = 0,
    long WarningCount = 0,
    long EstimatedBytes = 0,
    bool DryRun = false)
{
    public ProgressSnapshot Apply(OperationProgressEvent progressEvent)
    {
        ArgumentNullException.ThrowIfNull(progressEvent);

        if (progressEvent.Kind == ProgressEventKind.Warning)
        {
            return this with { WarningCount = WarningCount + 1 };
        }

        return progressEvent.Kind switch
        {
            ProgressEventKind.DiscoveryStarted => this with
            {
                Phase = progressEvent.Kind,
                OptionalPath = null,
                Current = 0,
                Total = 0,
                RepositoryCount = progressEvent.RepositoryCount,
            },
            ProgressEventKind.RepositoryFound => this with
            {
                Phase = progressEvent.Kind,
                OptionalPath = progressEvent.Path,
                RepositoryCount = progressEvent.RepositoryCount,
            },
            ProgressEventKind.DiscoveryCompleted => this with
            {
                Phase = progressEvent.Kind,
                OptionalPath = null,
                RepositoryCount = progressEvent.RepositoryCount,
            },
            ProgressEventKind.RepositoryScanStarted or ProgressEventKind.RepositoryScanCompleted => this with
            {
                Phase = progressEvent.Kind,
                OptionalPath = progressEvent.Path,
                Current = progressEvent.Current,
                Total = progressEvent.Total,
                CandidateCount = progressEvent.CandidateCount,
                EstimatedBytes = progressEvent.EstimatedBytes,
                DryRun = progressEvent.DryRun,
            },
            ProgressEventKind.CandidateStarted => this with
            {
                Phase = progressEvent.Kind,
                OptionalPath = progressEvent.Path,
                Current = progressEvent.Current,
                Total = progressEvent.Total,
                DeletedCount = progressEvent.DeletedCount,
                ValidatedCount = progressEvent.ValidatedCount,
                SkippedCount = progressEvent.SkippedCount,
                FailedCount = progressEvent.FailedCount,
                EstimatedBytes = progressEvent.EstimatedBytes,
                DryRun = progressEvent.DryRun,
            },
            ProgressEventKind.CandidateCompleted => ApplyCandidateResult(progressEvent),
            ProgressEventKind.Completed or ProgressEventKind.Interrupted => this with
            {
                Phase = progressEvent.Kind,
                OptionalPath = null,
                Current = progressEvent.Current,
                Total = progressEvent.Total,
                RepositoryCount = progressEvent.RepositoryCount,
                CandidateCount = progressEvent.CandidateCount,
                DeletedCount = progressEvent.DeletedCount,
                ValidatedCount = progressEvent.ValidatedCount,
                SkippedCount = progressEvent.SkippedCount,
                FailedCount = progressEvent.FailedCount,
                WarningCount = progressEvent.WarningCount,
                EstimatedBytes = progressEvent.EstimatedBytes,
                DryRun = progressEvent.DryRun,
            },
            ProgressEventKind.Failed => this with
            {
                Phase = progressEvent.Kind,
                OptionalPath = null,
            },
            _ => this,
        };
    }

    public string Format()
    {
        var status = Phase switch
        {
            ProgressEventKind.DiscoveryStarted or ProgressEventKind.RepositoryFound =>
                $"Discovering repositories • {RepositoryCount} found",
            ProgressEventKind.RepositoryScanStarted or ProgressEventKind.RepositoryScanCompleted =>
                $"Scanning repositories {Current}/{Total} • {CandidateCount} candidates • {ProgressText.FormatBytes(EstimatedBytes)}",
            ProgressEventKind.CandidateStarted or ProgressEventKind.CandidateCompleted when DryRun =>
                $"Validating artifacts {Current}/{Total} • {ValidatedCount} validated • {ProgressText.FormatBytes(EstimatedBytes)}",
            ProgressEventKind.CandidateStarted or ProgressEventKind.CandidateCompleted =>
                $"Cleaning artifacts {Current}/{Total} • {DeletedCount} deleted • {ProgressText.FormatBytes(EstimatedBytes)}",
            _ => string.Empty,
        };

        return WarningCount > 0 && status.Length > 0
            ? $"{status} • {WarningCount} warnings"
            : status;
    }

    private ProgressSnapshot ApplyCandidateResult(OperationProgressEvent progressEvent)
    {
        var outcomeAdvancesBytes = progressEvent.DryRun
            ? progressEvent.Outcome == ProgressCandidateOutcome.Validated
            : progressEvent.Outcome == ProgressCandidateOutcome.Deleted;

        return this with
        {
            Phase = progressEvent.Kind,
            OptionalPath = progressEvent.Path,
            Current = progressEvent.Current,
            Total = progressEvent.Total,
            DeletedCount = progressEvent.DeletedCount,
            ValidatedCount = progressEvent.ValidatedCount,
            SkippedCount = progressEvent.SkippedCount,
            FailedCount = progressEvent.FailedCount,
            EstimatedBytes = outcomeAdvancesBytes ? progressEvent.EstimatedBytes : EstimatedBytes,
            DryRun = progressEvent.DryRun,
        };
    }
}
