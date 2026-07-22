using DevCleaner.Git;
using DevCleaner.Scanning;

namespace DevCleaner.Cleaning;

public sealed class CleanupService
{
    private readonly GitClient git;
    private readonly FileTreeAnalyzer analyzer;
    private readonly ICleanupFileSystem fileSystem;
    private readonly CleanupBoundaryInspector boundaryInspector;
    private readonly QuarantineCleanup quarantineCleanup;

    public CleanupService(GitClient git)
        : this(git, null, null)
    {
    }

    internal CleanupService(
        GitClient git,
        FileTreeAnalyzer? analyzer = null,
        ICleanupFileSystem? fileSystem = null,
        ICleanupMutationObserver? mutationObserver = null,
        IFileSystemIdentityProvider? identityProvider = null,
        IAtomicFileMover? atomicFileMover = null)
    {
        this.git = git ?? throw new ArgumentNullException(nameof(git));
        var resolvedIdentityProvider = identityProvider ?? new FileSystemIdentityProvider();
        this.analyzer = analyzer ?? new FileTreeAnalyzer(resolvedIdentityProvider);
        this.fileSystem = fileSystem ?? new SystemCleanupFileSystem();
        boundaryInspector = new CleanupBoundaryInspector(this.fileSystem);
        quarantineCleanup = new QuarantineCleanup(
            this.analyzer,
            this.fileSystem,
            atomicFileMover ?? new NativeAtomicFileMover(),
            mutationObserver ?? new NullCleanupMutationObserver(),
            resolvedIdentityProvider,
            boundaryInspector);
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
                var validation = await ValidateAsync(candidate, requestedRoots, request.RuleCatalog, cancellationToken).ConfigureAwait(false);
                if (!validation.IsValid)
                {
                    results.Add(new CleanupCandidateResult(candidate, CleanupOutcome.Skipped, validation.Error!));
                    continue;
                }

                if (request.DryRun)
                {
                    results.Add(new CleanupCandidateResult(candidate, CleanupOutcome.Skipped, "Validated; dry run did not delete the candidate."));
                    continue;
                }

                results.Add(quarantineCleanup.Execute(
                    candidate,
                    validation.RequestedRoot!,
                    validation.CandidatePath!,
                    validation.Identity!,
                    validation.IsDirectory,
                    cancellationToken));
            }
            catch (CleanupMutationInterruptedException exception)
            {
                results.Add(exception.Result);
                interrupted = true;
                break;
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

        return new CleanupResult(Array.AsReadOnly(results.ToArray()), request.DryRun, interrupted, request.Candidates.Count);
    }

    private async Task<CandidateValidation> ValidateAsync(
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
            return CandidateValidation.Failure("Candidate is outside the requested root boundary.");
        }

        if (!RepositoryDiscovery.IsSameOrDescendant(candidatePath, repositoryRoot) ||
            string.Equals(candidatePath, repositoryRoot, PathComparison))
        {
            return CandidateValidation.Failure("Candidate is outside its repository root boundary.");
        }

        var boundaryError = boundaryInspector.Inspect(requestedRoot, candidatePath);
        if (boundaryError is not null) return CandidateValidation.Failure(boundaryError);

        var expectedPath = Path.GetFullPath(Path.Combine(repositoryRoot, candidate.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!string.Equals(candidatePath, expectedPath, PathComparison))
        {
            return CandidateValidation.Failure("Candidate absolute and repository-relative paths no longer identify the same location.");
        }

        FileAttributes attributes;
        try
        {
            attributes = fileSystem.GetAttributes(candidatePath);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return CandidateValidation.Failure($"Candidate no longer exists: {exception.Message}");
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            return CandidateValidation.Failure("Candidate is now a filesystem link, junction, or reparse point.");
        }

        var currentAnalysis = analyzer.Analyze(candidatePath, repositoryRoot, cancellationToken);
        if (!currentAnalysis.IsSafe || currentAnalysis.Identity is null)
        {
            var detail = currentAnalysis.Warnings.Count == 0
                ? "Candidate failed filesystem safety analysis."
                : string.Join(" ", currentAnalysis.Warnings.Select(warning => warning.Message));
            return CandidateValidation.Failure(detail);
        }

        if (!CleanupIdentity.HasSameStableIdentity(candidate.Identity, currentAnalysis.Identity))
        {
            return CandidateValidation.Failure("Candidate filesystem identity changed after the scan.");
        }

        var visiblePaths = await git.ListVisibleFilesAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
        var normalizedRelativePath = NormalizeRelativePath(candidate.RelativePath);
        if (await git.ContainsTrackedContentAsync(repositoryRoot, normalizedRelativePath, cancellationToken).ConfigureAwait(false))
        {
            return CandidateValidation.Failure("Candidate now contains tracked or otherwise visible content.");
        }

        if (!await git.IsIgnoredAsync(repositoryRoot, normalizedRelativePath, cancellationToken).ConfigureAwait(false))
        {
            return CandidateValidation.Failure("Git no longer reports the candidate as ignored.");
        }

        if (ContainsVisibleContent(normalizedRelativePath, visiblePaths))
        {
            return CandidateValidation.Failure("Candidate now contains tracked or otherwise visible content.");
        }

        var rule = ruleCatalog.Rules.FirstOrDefault(ruleValue =>
            string.Equals(ruleValue.Id, candidate.RuleId, StringComparison.Ordinal) &&
            ruleValue.Category == candidate.Category &&
            ruleValue.Matches(normalizedRelativePath) &&
            ruleValue.IsActiveFor(visiblePaths));
        if (rule is null)
        {
            return CandidateValidation.Failure("The captured cleanup rule is no longer active and matching.");
        }

        return CandidateValidation.Success(
            requestedRoot,
            candidatePath,
            currentAnalysis.Identity,
            (attributes & FileAttributes.Directory) != 0);
    }

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

    private sealed record CandidateValidation(
        bool IsValid,
        string? Error,
        string? RequestedRoot,
        string? CandidatePath,
        FileSystemIdentity? Identity,
        bool IsDirectory)
    {
        public static CandidateValidation Failure(string error) => new(false, error, null, null, null, false);

        public static CandidateValidation Success(
            string requestedRoot,
            string candidatePath,
            FileSystemIdentity identity,
            bool isDirectory) =>
            new(true, null, requestedRoot, candidatePath, identity, isDirectory);
    }
}
