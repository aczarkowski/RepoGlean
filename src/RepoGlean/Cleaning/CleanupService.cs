using RepoGlean.Git;
using RepoGlean.Progress;
using RepoGlean.Scanning;

namespace RepoGlean.Cleaning;

public sealed class CleanupService
{
    private readonly CleanupAuthorityValidator authorityValidator;
    private readonly QuarantineCleanup quarantineCleanup;
    private readonly IOperationProgress progress;

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
        IAtomicFileMover? atomicFileMover = null,
        IOperationProgress? progress = null)
    {
        ArgumentNullException.ThrowIfNull(git);
        this.progress = progress ?? NullOperationProgress.Instance;
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
        long deletedCount = 0;
        long validatedCount = 0;
        long skippedCount = 0;
        long failedCount = 0;
        long processedEstimatedBytes = 0;

        void RecordResult(CleanupCandidateResult result, bool validated = false)
        {
            results.Add(result);
            var outcome = validated
                ? ProgressCandidateOutcome.Validated
                : result.DeletionCompleted
                    ? ProgressCandidateOutcome.Deleted
                    : result.Outcome == CleanupOutcome.Skipped
                        ? ProgressCandidateOutcome.Skipped
                        : ProgressCandidateOutcome.Failed;
            switch (outcome)
            {
                case ProgressCandidateOutcome.Deleted:
                    deletedCount++;
                    processedEstimatedBytes = FileTreeAnalyzer.SaturatingAdd(
                        processedEstimatedBytes,
                        result.Candidate.EstimatedBytes);
                    break;
                case ProgressCandidateOutcome.Validated:
                    validatedCount++;
                    processedEstimatedBytes = FileTreeAnalyzer.SaturatingAdd(
                        processedEstimatedBytes,
                        result.Candidate.EstimatedBytes);
                    break;
                case ProgressCandidateOutcome.Skipped:
                    skippedCount++;
                    break;
                case ProgressCandidateOutcome.Failed:
                    failedCount++;
                    break;
            }

            ReportProgress(new OperationProgressEvent(
                ProgressEventKind.CandidateCompleted,
                ProgressOperation.Clean,
                Path: result.Candidate.AbsolutePath,
                Current: results.Count,
                Total: request.Candidates.Count,
                DeletedCount: deletedCount,
                ValidatedCount: validatedCount,
                SkippedCount: skippedCount,
                FailedCount: failedCount,
                EstimatedBytes: processedEstimatedBytes,
                DryRun: request.DryRun,
                Outcome: outcome));
        }

        foreach (var candidate in request.Candidates)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                interrupted = true;
                break;
            }

            ReportProgress(new OperationProgressEvent(
                ProgressEventKind.CandidateStarted,
                ProgressOperation.Clean,
                Path: candidate.AbsolutePath,
                Current: results.Count + 1,
                Total: request.Candidates.Count,
                DeletedCount: deletedCount,
                ValidatedCount: validatedCount,
                SkippedCount: skippedCount,
                FailedCount: failedCount,
                EstimatedBytes: processedEstimatedBytes,
                DryRun: request.DryRun));
            try
            {
                var validation = await authorityValidator
                    .ValidateInitialAsync(candidate, requestedRoots, request.RuleCatalog, cancellationToken)
                    .ConfigureAwait(false);
                if (!validation.IsValid)
                {
                    RecordResult(new CleanupCandidateResult(candidate, CleanupOutcome.Skipped, validation.Error!));
                    continue;
                }

                if (request.DryRun)
                {
                    RecordResult(
                        new CleanupCandidateResult(
                            candidate,
                            CleanupOutcome.Skipped,
                            "Validated; dry run did not delete the candidate."),
                        validated: true);
                    continue;
                }

                RecordResult(await quarantineCleanup.ExecuteAsync(
                    candidate,
                    validation,
                    request.RuleCatalog,
                    cancellationToken).ConfigureAwait(false));
            }
            catch (CleanupMutationInterruptedException exception)
            {
                RecordResult(exception.Result);
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
                RecordResult(new CleanupCandidateResult(candidate, CleanupOutcome.Failed, exception.Message));
            }
        }

        return new CleanupResult(
            Array.AsReadOnly(results.ToArray()),
            request.DryRun,
            interrupted,
            request.Candidates.Count);
    }

    private void ReportProgress(OperationProgressEvent progressEvent)
    {
        try
        {
            progress.Report(progressEvent);
        }
        catch (Exception exception) when (IsRecoverableProgressException(exception))
        {
        }
    }

    private static bool IsRecoverableProgressException(Exception exception) =>
        exception is not OutOfMemoryException
        and not StackOverflowException
        and not AccessViolationException;

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
