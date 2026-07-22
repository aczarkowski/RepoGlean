using DevCleaner.Git;
using DevCleaner.Scanning;

namespace DevCleaner.Cleaning;

internal interface ICleanupFileSystem
{
    FileAttributes GetAttributes(string path);

    IReadOnlyList<string> GetFileSystemEntries(string path);

    void DeleteFile(string path);

    void DeleteDirectory(string path);
}

internal sealed class SystemCleanupFileSystem : ICleanupFileSystem
{
    public FileAttributes GetAttributes(string path) => File.GetAttributes(path);

    public IReadOnlyList<string> GetFileSystemEntries(string path) => Directory.GetFileSystemEntries(path);

    public void DeleteFile(string path) => File.Delete(path);

    public void DeleteDirectory(string path) => Directory.Delete(path, recursive: false);
}

public sealed class CleanupService
{
    private readonly GitClient git;
    private readonly FileTreeAnalyzer analyzer;
    private readonly ICleanupFileSystem fileSystem;

    public CleanupService(GitClient git)
        : this(git, null, null)
    {
    }

    internal CleanupService(
        GitClient git,
        FileTreeAnalyzer? analyzer = null,
        ICleanupFileSystem? fileSystem = null)
    {
        this.git = git ?? throw new ArgumentNullException(nameof(git));
        this.analyzer = analyzer ?? new FileTreeAnalyzer();
        this.fileSystem = fileSystem ?? new SystemCleanupFileSystem();
    }

    public async Task<CleanupResult> ExecuteAsync(
        CleanupRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Candidates);
        ArgumentNullException.ThrowIfNull(request.RequestedRoots);
        ArgumentNullException.ThrowIfNull(request.RuleCatalog);

        var requestedRoots = request.RequestedRoots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(Path.GetFullPath)
            .Distinct(PathComparer)
            .ToArray();
        var results = new List<CleanupCandidateResult>();
        var interrupted = cancellationToken.IsCancellationRequested;

        foreach (var candidate in request.Candidates)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                interrupted = true;
                break;
            }

            try
            {
                var validationError = await ValidateAsync(candidate, requestedRoots, request.RuleCatalog, cancellationToken).ConfigureAwait(false);
                if (validationError is not null)
                {
                    results.Add(new CleanupCandidateResult(candidate, CleanupOutcome.Skipped, validationError));
                    continue;
                }

                if (request.DryRun)
                {
                    results.Add(new CleanupCandidateResult(candidate, CleanupOutcome.Skipped, "Validated; dry run did not delete the candidate."));
                    continue;
                }

                DeleteWithoutFollowingLinks(candidate.AbsolutePath, cancellationToken);
                results.Add(new CleanupCandidateResult(candidate, CleanupOutcome.Deleted, "Deleted after safety revalidation."));
            }
            catch (OperationCanceledException)
            {
                interrupted = true;
                break;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or GitCommandException or ArgumentException)
            {
                results.Add(new CleanupCandidateResult(candidate, CleanupOutcome.Failed, exception.Message));
            }
        }

        return new CleanupResult(Array.AsReadOnly(results.ToArray()), request.DryRun, interrupted);
    }

    private async Task<string?> ValidateAsync(
        ArtifactCandidate candidate,
        IReadOnlyList<string> requestedRoots,
        DevCleaner.Rules.RuleCatalog ruleCatalog,
        CancellationToken cancellationToken)
    {
        var repositoryRoot = Path.GetFullPath(candidate.RepositoryRoot);
        var candidatePath = Path.GetFullPath(candidate.AbsolutePath);
        var requestedRoot = requestedRoots
            .Where(root =>
                RepositoryDiscovery.IsSameOrDescendant(repositoryRoot, root) &&
                RepositoryDiscovery.IsSameOrDescendant(candidatePath, root))
            .OrderByDescending(root => root.Length)
            .FirstOrDefault();
        if (requestedRoot is null)
        {
            return "Candidate is outside the requested root boundary.";
        }

        if (!RepositoryDiscovery.IsSameOrDescendant(candidatePath, repositoryRoot) ||
            string.Equals(candidatePath, repositoryRoot, PathComparison))
        {
            return "Candidate is outside its repository root boundary.";
        }

        var boundaryError = InspectBoundaryComponents(requestedRoot, candidatePath);
        if (boundaryError is not null) return boundaryError;

        var expectedPath = Path.GetFullPath(Path.Combine(repositoryRoot, candidate.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!string.Equals(candidatePath, expectedPath, PathComparison))
        {
            return "Candidate absolute and repository-relative paths no longer identify the same location.";
        }

        FileAttributes attributes;
        try
        {
            attributes = fileSystem.GetAttributes(candidatePath);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return $"Candidate no longer exists: {exception.Message}";
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            return "Candidate is now a filesystem link, junction, or reparse point.";
        }

        var currentAnalysis = analyzer.Analyze(candidatePath, repositoryRoot, cancellationToken);
        if (!currentAnalysis.IsSafe || currentAnalysis.Identity is null)
        {
            var detail = currentAnalysis.Warnings.Count == 0
                ? "Candidate failed filesystem safety analysis."
                : string.Join(" ", currentAnalysis.Warnings.Select(warning => warning.Message));
            return detail;
        }

        if (!HasSameStableIdentity(candidate.Identity, currentAnalysis.Identity))
        {
            return "Candidate filesystem identity changed after the scan.";
        }

        var visiblePaths = await git.ListVisibleFilesAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
        var normalizedRelativePath = NormalizeRelativePath(candidate.RelativePath);
        if (await git.ContainsTrackedContentAsync(repositoryRoot, normalizedRelativePath, cancellationToken).ConfigureAwait(false))
        {
            return "Candidate now contains tracked or otherwise visible content.";
        }

        if (!await git.IsIgnoredAsync(repositoryRoot, normalizedRelativePath, cancellationToken).ConfigureAwait(false))
        {
            return "Git no longer reports the candidate as ignored.";
        }

        if (ContainsVisibleContent(normalizedRelativePath, visiblePaths))
        {
            return "Candidate now contains tracked or otherwise visible content.";
        }

        var rule = ruleCatalog.Rules.FirstOrDefault(ruleValue =>
            string.Equals(ruleValue.Id, candidate.RuleId, StringComparison.Ordinal) &&
            ruleValue.Category == candidate.Category &&
            ruleValue.Matches(normalizedRelativePath) &&
            ruleValue.IsActiveFor(visiblePaths));
        if (rule is null)
        {
            return "The captured cleanup rule is no longer active and matching.";
        }

        return null;
    }

    private string? InspectBoundaryComponents(string requestedRoot, string candidatePath)
    {
        var relativePath = Path.GetRelativePath(requestedRoot, candidatePath);
        var currentPath = requestedRoot;
        foreach (var component in new[] { string.Empty }.Concat(relativePath.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries)))
        {
            if (component.Length > 0) currentPath = Path.Combine(currentPath, component);
            FileAttributes attributes;
            try
            {
                attributes = fileSystem.GetAttributes(currentPath);
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                return $"Cleanup boundary component no longer exists: {exception.Message}";
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                return $"Cleanup boundary contains a filesystem link, junction, or reparse point: {currentPath}";
            }
        }

        return null;
    }

    private void DeleteWithoutFollowingLinks(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var attributes = fileSystem.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException($"Refusing to delete filesystem link or reparse point '{path}'.");
        }

        if ((attributes & FileAttributes.Directory) == 0)
        {
            fileSystem.DeleteFile(path);
            return;
        }

        foreach (var entry in fileSystem.GetFileSystemEntries(path).OrderBy(value => value, PathComparer))
        {
            DeleteWithoutFollowingLinks(entry, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        fileSystem.DeleteDirectory(path);
    }

    private static bool HasSameStableIdentity(FileSystemIdentity captured, FileSystemIdentity current) =>
        captured.VolumeId == current.VolumeId &&
        captured.FileId == current.FileId &&
        string.Equals(captured.MountId, current.MountId, StringComparison.Ordinal);

    private static bool ContainsVisibleContent(string candidateRelativePath, IReadOnlyList<string> visiblePaths)
    {
        var prefix = candidateRelativePath.EndsWith("/", StringComparison.Ordinal) ? candidateRelativePath : $"{candidateRelativePath}/";
        return visiblePaths.Any(path =>
            string.Equals(path, candidateRelativePath, StringComparison.Ordinal) ||
            path.StartsWith(prefix, StringComparison.Ordinal));
    }

    private static string NormalizeRelativePath(string path) => path.Replace('\\', '/').TrimStart('/');

    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
