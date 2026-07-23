using DevCleaner.Git;
using DevCleaner.Rules;
using DevCleaner.Scanning;

namespace DevCleaner.Cleaning;

internal sealed class CleanupAuthorityValidator(
    GitClient git,
    FileTreeAnalyzer analyzer,
    ICleanupFileSystem fileSystem,
    IFileSystemIdentityProvider identityProvider,
    CleanupBoundaryInspector boundaryInspector)
{
    public Task<CleanupAuthorityValidation> ValidateInitialAsync(
        ArtifactCandidate candidate,
        IReadOnlyList<string> requestedRoots,
        RuleCatalog ruleCatalog,
        CancellationToken cancellationToken) =>
        ValidateAsync(candidate, requestedRoots, ruleCatalog, candidatePresent: true, quarantinePath: null, cancellationToken);

    public Task<CleanupAuthorityValidation> RevalidateAsync(
        ArtifactCandidate candidate,
        string requestedRoot,
        RuleCatalog ruleCatalog,
        bool candidatePresent,
        string? quarantinePath,
        CancellationToken cancellationToken) =>
        ValidateAsync(candidate, [requestedRoot], ruleCatalog, candidatePresent, quarantinePath, cancellationToken);

    private async Task<CleanupAuthorityValidation> ValidateAsync(
        ArtifactCandidate candidate,
        IReadOnlyList<string> requestedRoots,
        RuleCatalog ruleCatalog,
        bool candidatePresent,
        string? quarantinePath,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
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
                return CleanupAuthorityValidation.Failure("Candidate is outside the requested root boundary.");
            }

            if (!RepositoryDiscovery.IsSameOrDescendant(candidatePath, repositoryRoot) ||
                string.Equals(candidatePath, repositoryRoot, PathComparison))
            {
                return CleanupAuthorityValidation.Failure("Candidate is outside its repository root boundary.");
            }

            var expectedPath = Path.GetFullPath(Path.Combine(
                repositoryRoot,
                candidate.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!string.Equals(candidatePath, expectedPath, PathComparison))
            {
                return CleanupAuthorityValidation.Failure("Candidate absolute and repository-relative paths no longer identify the same location.");
            }

            var repositoryBoundaryError = boundaryInspector.Inspect(requestedRoot, repositoryRoot);
            if (repositoryBoundaryError is not null) return CleanupAuthorityValidation.Failure(repositoryBoundaryError);

            if (!identityProvider.TryGetIdentity(repositoryRoot, out var repositoryIdentity, out var repositoryIdentityError) ||
                repositoryIdentity is null)
            {
                return CleanupAuthorityValidation.Failure(repositoryIdentityError ?? "Repository identity is unavailable.");
            }

            if (!CleanupIdentity.IsDirectory(repositoryIdentity) ||
                !CleanupIdentity.HasSameStableIdentity(candidate.RepositoryIdentity, repositoryIdentity))
            {
                return CleanupAuthorityValidation.Failure("Repository root identity, type, or filesystem mount changed after the scan.");
            }

            var actualRepositoryRoot = await git.GetRepositoryRootAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
            if (!identityProvider.TryGetIdentity(actualRepositoryRoot, out var gitRepositoryIdentity, out var gitRepositoryIdentityError) ||
                gitRepositoryIdentity is null)
            {
                return CleanupAuthorityValidation.Failure(
                    gitRepositoryIdentityError ?? "Git repository root identity is unavailable.");
            }

            if (!CleanupIdentity.HasSameStableIdentity(candidate.RepositoryIdentity, gitRepositoryIdentity))
            {
                return CleanupAuthorityValidation.Failure("Git repository boundary no longer matches the scanned repository root.");
            }

            var inspectedPath = candidatePresent ? candidatePath : Path.GetDirectoryName(candidatePath)!;
            var candidateBoundaryError = boundaryInspector.Inspect(repositoryRoot, inspectedPath);
            if (candidateBoundaryError is not null) return CleanupAuthorityValidation.Failure(candidateBoundaryError);

            FileSystemIdentity candidateIdentity;
            bool isDirectory;
            if (candidatePresent)
            {
                FileAttributes attributes;
                try
                {
                    attributes = fileSystem.GetAttributes(candidatePath);
                }
                catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
                {
                    return CleanupAuthorityValidation.Failure($"Candidate no longer exists: {exception.Message}");
                }

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return CleanupAuthorityValidation.Failure("Candidate is now a filesystem link, junction, or reparse point.");
                }

                var currentAnalysis = analyzer.Analyze(candidatePath, repositoryRoot, cancellationToken);
                if (!currentAnalysis.IsSafe || currentAnalysis.Identity is null || currentAnalysis.RepositoryIdentity is null)
                {
                    var detail = currentAnalysis.Warnings.Count == 0
                        ? "Candidate failed filesystem safety analysis."
                        : string.Join(" ", currentAnalysis.Warnings.Select(warning => warning.Message));
                    return CleanupAuthorityValidation.Failure(detail);
                }

                if (!CleanupIdentity.HasSameStableIdentity(candidate.Identity, currentAnalysis.Identity))
                {
                    return CleanupAuthorityValidation.Failure("Candidate filesystem identity changed after the scan.");
                }

                if (!CleanupIdentity.HasSameStableIdentity(candidate.RepositoryIdentity, currentAnalysis.RepositoryIdentity))
                {
                    return CleanupAuthorityValidation.Failure("Repository root identity changed during candidate analysis.");
                }

                candidateIdentity = currentAnalysis.Identity;
                isDirectory = (attributes & FileAttributes.Directory) != 0;
            }
            else
            {
                if (boundaryInspector.EntryExists(candidatePath))
                {
                    return CleanupAuthorityValidation.Failure("Original candidate path is no longer absent after quarantine ownership.");
                }

                candidateIdentity = candidate.Identity;
                isDirectory = CleanupIdentity.IsDirectory(candidate.Identity);
            }

            var normalizedRelativePath = NormalizeRelativePath(candidate.RelativePath);
            string? quarantineRelativePath = null;
            if (quarantinePath is not null)
            {
                var fullQuarantinePath = Path.GetFullPath(quarantinePath);
                if (!RepositoryDiscovery.IsSameOrDescendant(fullQuarantinePath, repositoryRoot) ||
                    string.Equals(fullQuarantinePath, repositoryRoot, PathComparison))
                {
                    return CleanupAuthorityValidation.Failure("Cleanup quarantine is outside the repository boundary.");
                }

                quarantineRelativePath = NormalizeRelativePath(Path.GetRelativePath(repositoryRoot, fullQuarantinePath));
                if (await git.ContainsTrackedContentAsync(repositoryRoot, quarantineRelativePath, cancellationToken).ConfigureAwait(false))
                {
                    return CleanupAuthorityValidation.Failure("Cleanup quarantine now contains tracked or staged content.");
                }
            }

            var visiblePaths = quarantineRelativePath is null
                ? await git.ListVisibleFilesAsync(repositoryRoot, cancellationToken).ConfigureAwait(false)
                : await git.ListVisibleFilesExcludingAsync(repositoryRoot, quarantineRelativePath, cancellationToken).ConfigureAwait(false);
            if (await git.ContainsTrackedContentAsync(repositoryRoot, normalizedRelativePath, cancellationToken).ConfigureAwait(false))
            {
                return CleanupAuthorityValidation.Failure("Candidate now contains tracked or otherwise visible content.");
            }

            var ignorePath = !candidatePresent && isDirectory ? $"{normalizedRelativePath.TrimEnd('/')}/" : normalizedRelativePath;
            var isIgnored = candidatePresent
                ? await git.IsIgnoredAsync(repositoryRoot, ignorePath, cancellationToken).ConfigureAwait(false)
                : await git.IsIgnoredWithoutIndexAsync(repositoryRoot, ignorePath, cancellationToken).ConfigureAwait(false);
            if (!isIgnored)
            {
                return CleanupAuthorityValidation.Failure("Git no longer reports the candidate as ignored.");
            }

            if (ContainsVisibleContent(normalizedRelativePath, visiblePaths))
            {
                return CleanupAuthorityValidation.Failure("Candidate now contains tracked or otherwise visible content.");
            }

            var rule = ruleCatalog.Rules.FirstOrDefault(ruleValue =>
                string.Equals(ruleValue.Id, candidate.RuleId, StringComparison.Ordinal) &&
                ruleValue.Category == candidate.Category &&
                ruleValue.Matches(normalizedRelativePath) &&
                ruleValue.IsActiveFor(visiblePaths));
            if (rule is null)
            {
                return CleanupAuthorityValidation.Failure("The captured cleanup rule is no longer active and matching.");
            }

            return CleanupAuthorityValidation.Success(
                requestedRoot,
                repositoryRoot,
                candidatePath,
                repositoryIdentity,
                candidateIdentity,
                isDirectory);
        }
        catch (Exception exception) when (exception is
            IOException or
            UnauthorizedAccessException or
            System.Security.SecurityException or
            GitCommandException or
            GitUnavailableException or
            ArgumentException)
        {
            return CleanupAuthorityValidation.Failure($"Cleanup authority validation failed: {exception.Message}");
        }
    }

    private static bool ContainsVisibleContent(string candidateRelativePath, IReadOnlyList<string> visiblePaths)
    {
        var prefix = candidateRelativePath.EndsWith("/", StringComparison.Ordinal)
            ? candidateRelativePath
            : $"{candidateRelativePath}/";
        return visiblePaths.Any(path =>
            string.Equals(path, candidateRelativePath, StringComparison.Ordinal) ||
            path.StartsWith(prefix, StringComparison.Ordinal));
    }

    private static string NormalizeRelativePath(string path) => path.Replace('\\', '/').TrimStart('/');

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}

internal sealed record CleanupAuthorityValidation(
    bool IsValid,
    string? Error,
    string? RequestedRoot,
    string? RepositoryRoot,
    string? CandidatePath,
    FileSystemIdentity? RepositoryIdentity,
    FileSystemIdentity? CandidateIdentity,
    bool IsDirectory)
{
    public static CleanupAuthorityValidation Failure(string error) =>
        new(false, error, null, null, null, null, null, false);

    public static CleanupAuthorityValidation Success(
        string requestedRoot,
        string repositoryRoot,
        string candidatePath,
        FileSystemIdentity repositoryIdentity,
        FileSystemIdentity candidateIdentity,
        bool isDirectory) =>
        new(true, null, requestedRoot, repositoryRoot, candidatePath, repositoryIdentity, candidateIdentity, isDirectory);
}
