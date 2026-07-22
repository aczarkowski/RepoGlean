using DevCleaner.Rules;
using DevCleaner.Scanning;

namespace DevCleaner.Cleaning;

public enum CleanupOutcome
{
    Deleted,
    Skipped,
    Failed,
}

public sealed record CleanupCandidateResult(
    ArtifactCandidate Candidate,
    CleanupOutcome Outcome,
    string Message);

public sealed record CleanupRequest(
    IReadOnlyList<ArtifactCandidate> Candidates,
    IReadOnlyList<string> RequestedRoots,
    RuleCatalog RuleCatalog,
    bool DryRun);

public sealed record CleanupResult(
    IReadOnlyList<CleanupCandidateResult> Items,
    bool DryRun,
    bool IsInterrupted,
    long SelectedCount)
{
    public long DeletedCount => Items.LongCount(item => item.Outcome == CleanupOutcome.Deleted);

    public long SkippedCount => Items.LongCount(item => item.Outcome == CleanupOutcome.Skipped);

    public long FailedCount => Items.LongCount(item => item.Outcome == CleanupOutcome.Failed);

    public long EstimatedDeletedBytes => Items
        .Where(item => item.Outcome == CleanupOutcome.Deleted)
        .Aggregate(0L, (total, item) => FileTreeAnalyzer.SaturatingAdd(total, item.Candidate.EstimatedBytes));
}
