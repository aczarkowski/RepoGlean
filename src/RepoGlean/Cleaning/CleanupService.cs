using RepoGlean.Git;
using RepoGlean.Scanning;

namespace RepoGlean.Cleaning;

public sealed class CleanupService
{
    private readonly CleanupAuthorityValidator authorityValidator;
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
        ArgumentNullException.ThrowIfNull(git);
        var resolvedIdentityProvider = identityProvider ?? new FileSystemIdentityProvider();
        var resolvedAnalyzer = analyzer ?? new FileTreeAnalyzer(resolvedIdentityProvider);
        var resolvedFileSystem = fileSystem ?? new SystemCleanupFileSystem();
        var boundaryInspector = new CleanupBoundaryInspector(resolvedFileSystem);
        authorityValidator = new CleanupAuthorityValidator(
            git,
            resolvedAnalyzer,
            resolvedFileSystem,
            resolvedIdentityProvider,
            boundaryInspector);
        quarantineCleanup = new QuarantineCleanup(
            resolvedFileSystem,
            atomicFileMover ?? new NativeAtomicFileMover(),
            mutationObserver ?? new NullCleanupMutationObserver(),
            resolvedIdentityProvider,
            boundaryInspector,
            authorityValidator,
            new OwnedTreeInspector(resolvedFileSystem, resolvedIdentityProvider),
            new BoundaryAwareDeleter(resolvedFileSystem, resolvedIdentityProvider));
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
                var validation = await authorityValidator
                    .ValidateInitialAsync(candidate, requestedRoots, request.RuleCatalog, cancellationToken)
                    .ConfigureAwait(false);
                if (!validation.IsValid)
                {
                    results.Add(new CleanupCandidateResult(candidate, CleanupOutcome.Skipped, validation.Error!));
                    continue;
                }

                if (request.DryRun)
                {
                    results.Add(new CleanupCandidateResult(
                        candidate,
                        CleanupOutcome.Skipped,
                        "Validated; dry run did not delete the candidate."));
                    continue;
                }

                results.Add(await quarantineCleanup.ExecuteAsync(
                    candidate,
                    validation,
                    request.RuleCatalog,
                    cancellationToken).ConfigureAwait(false));
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
            catch (Exception exception) when (exception is
                IOException or
                UnauthorizedAccessException or
                GitCommandException or
                ArgumentException)
            {
                results.Add(new CleanupCandidateResult(candidate, CleanupOutcome.Failed, exception.Message));
            }
        }

        return new CleanupResult(
            Array.AsReadOnly(results.ToArray()),
            request.DryRun,
            interrupted,
            request.Candidates.Count);
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
