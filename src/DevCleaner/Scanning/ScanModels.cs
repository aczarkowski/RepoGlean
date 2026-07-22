using DevCleaner.Cli;

namespace DevCleaner.Scanning;

public sealed record OperationWarning(string Path, string Message);

public sealed record FileSystemIdentity(
    FileAttributes Attributes,
    DateTime CreationTimeUtc,
    DateTime LastWriteTimeUtc,
    long Length,
    string? LinkTarget);

public sealed record ArtifactCandidate(
    string RepositoryRoot,
    string AbsolutePath,
    string RelativePath,
    string RuleId,
    ArtifactCategory Category,
    bool Preselected,
    long FileCount,
    long EstimatedBytes,
    FileSystemIdentity Identity);

public sealed record RepositoryScanResult(
    string RepositoryRoot,
    IReadOnlyList<ArtifactCandidate> Candidates,
    long FileCount,
    long EstimatedBytes,
    IReadOnlyList<OperationWarning> Warnings);

public sealed record ScanResult(
    IReadOnlyList<RepositoryScanResult> Repositories,
    long FileCount,
    long EstimatedBytes,
    IReadOnlyList<OperationWarning> Warnings);

public sealed record RepositoryDiscoveryResult(
    IReadOnlyList<string> Repositories,
    IReadOnlyList<OperationWarning> Warnings);

public sealed record ScanOptions(
    IReadOnlyList<string> RepositoryFilters,
    IReadOnlyList<ArtifactCategory> CategoryFilters,
    IReadOnlyList<string> Exclusions,
    long? MinimumBytes)
{
    public static ScanOptions Default { get; } = new([], [], [], null);
}
