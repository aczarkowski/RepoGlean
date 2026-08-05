using RepoGlean.Git;
using RepoGlean.Scanning;

namespace RepoGlean.Auditing;

public sealed record AuditOptions(
    IReadOnlyList<string> RepositoryFilters,
    IReadOnlyList<string> Exclusions,
    long MinimumBytes)
{
    public const long DefaultMinimumBytes = 100L * 1024 * 1024;
}

public sealed record AuditFinding(
    string RepositoryRoot,
    string AbsolutePath,
    string RelativePath,
    long FileCount,
    long EstimatedBytes,
    DateTimeOffset? NewestWriteTimeUtc,
    GitIgnoreMatch Ignore);

public sealed record RepositoryAuditResult(
    string RepositoryRoot,
    IReadOnlyList<AuditFinding> Findings,
    long FileCount,
    long EstimatedBytes,
    IReadOnlyList<OperationWarning> Warnings);

public sealed record AuditResult(
    IReadOnlyList<RepositoryAuditResult> Repositories,
    long FileCount,
    long EstimatedBytes,
    IReadOnlyList<OperationWarning> Warnings);
