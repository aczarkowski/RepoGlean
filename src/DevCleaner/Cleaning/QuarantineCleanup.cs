using DevCleaner.Scanning;

namespace DevCleaner.Cleaning;

internal sealed class QuarantineCleanup(
    FileTreeAnalyzer analyzer,
    ICleanupFileSystem fileSystem,
    ICleanupMutationObserver mutationObserver,
    IFileSystemIdentityProvider identityProvider,
    CleanupBoundaryInspector boundaryInspector)
{
    public CleanupCandidateResult Execute(
        ArtifactCandidate candidate,
        string requestedRoot,
        string candidatePath,
        FileSystemIdentity candidateIdentity,
        bool isDirectory,
        CancellationToken cancellationToken)
    {
        var quarantineRoot = Path.Combine(requestedRoot, $".devcleaner-quarantine-{Guid.NewGuid():N}");
        var destinationPath = Path.Combine(quarantineRoot, "payload");
        fileSystem.CreateDirectory(quarantineRoot);
        if (!TryGetIdentity(quarantineRoot, out var quarantineIdentity, out var quarantineError) || quarantineIdentity is null)
        {
            _ = TryRemoveEmptyQuarantine(quarantineRoot, null, out _);
            return new CleanupCandidateResult(candidate, CleanupOutcome.Failed, quarantineError ?? "Unable to identify the cleanup quarantine directory.");
        }

        if (!CleanupIdentity.IsDirectory(quarantineIdentity) || !CleanupIdentity.IsSameMount(candidateIdentity, quarantineIdentity))
        {
            _ = TryRemoveEmptyQuarantine(quarantineRoot, quarantineIdentity, out _);
            return new CleanupCandidateResult(candidate, CleanupOutcome.Failed, "Cleanup quarantine is not a safe directory on the candidate filesystem mount.");
        }

        var moved = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            mutationObserver.BeforeQuarantineMove(candidate, quarantineRoot, destinationPath);
            cancellationToken.ThrowIfCancellationRequested();
            fileSystem.Move(candidatePath, destinationPath, isDirectory);
            moved = true;
            mutationObserver.BeforeMovedIdentityCheck(candidate, quarantineRoot, destinationPath);

            var movedIdentityAvailable = TryGetIdentity(destinationPath, out var movedIdentity, out var movedIdentityError);
            if (!HasIdentity(quarantineRoot, quarantineIdentity) || !movedIdentityAvailable || movedIdentity is null)
            {
                return RecoverOrStrand(candidate, requestedRoot, candidatePath, quarantineRoot, quarantineIdentity, destinationPath, movedIdentityError ?? "Quarantine ownership changed after the atomic move.");
            }

            if (!CleanupIdentity.HasSameStableIdentity(candidateIdentity, movedIdentity) ||
                isDirectory != CleanupIdentity.IsDirectory(movedIdentity))
            {
                return RecoverOrStrand(candidate, requestedRoot, candidatePath, quarantineRoot, quarantineIdentity, destinationPath, "Moved object identity or type does not match the validated candidate.");
            }

            var movedAnalysis = analyzer.Analyze(destinationPath, requestedRoot, cancellationToken);
            if (!movedAnalysis.IsSafe || movedAnalysis.Identity is null || !CleanupIdentity.HasSameStableIdentity(movedIdentity, movedAnalysis.Identity))
            {
                var detail = movedAnalysis.Warnings.Count == 0
                    ? "Moved object failed quarantine safety analysis."
                    : string.Join(" ", movedAnalysis.Warnings.Select(warning => warning.Message));
                return RecoverOrStrand(candidate, requestedRoot, candidatePath, quarantineRoot, quarantineIdentity, destinationPath, detail);
            }

            mutationObserver.BeforeRecursiveDelete(candidate, quarantineRoot, destinationPath);
            if (!HasIdentity(quarantineRoot, quarantineIdentity) || !HasIdentity(destinationPath, movedIdentity))
            {
                return RecoverOrStrand(candidate, requestedRoot, candidatePath, quarantineRoot, quarantineIdentity, destinationPath, "Quarantine ownership changed before permanent deletion.");
            }

            fileSystem.DeleteOwnedObject(destinationPath, isDirectory, cancellationToken);
            moved = false;
            if (!TryRemoveEmptyQuarantine(quarantineRoot, quarantineIdentity, out var cleanupError))
            {
                return new CleanupCandidateResult(
                    candidate,
                    CleanupOutcome.Failed,
                    $"Candidate was permanently deleted, but its empty quarantine could not be removed and remains at '{quarantineRoot}': {cleanupError}");
            }

            return new CleanupCandidateResult(candidate, CleanupOutcome.Deleted, "Deleted after atomic quarantine ownership and safety revalidation.");
        }
        catch (OperationCanceledException exception) when (moved)
        {
            var result = RecoverOrStrand(
                candidate,
                requestedRoot,
                candidatePath,
                quarantineRoot,
                quarantineIdentity,
                destinationPath,
                "Cleanup was interrupted after the candidate entered quarantine and may have been partially mutated.");
            throw new CleanupMutationInterruptedException(result, exception);
        }
        catch (Exception exception) when (moved && exception is IOException or UnauthorizedAccessException)
        {
            return RecoverOrStrand(
                candidate,
                requestedRoot,
                candidatePath,
                quarantineRoot,
                quarantineIdentity,
                destinationPath,
                $"Permanent deletion failed after quarantine ownership: {exception.Message}");
        }
        catch
        {
            if (!moved) _ = TryRemoveEmptyQuarantine(quarantineRoot, quarantineIdentity, out _);
            throw;
        }
    }

    private CleanupCandidateResult RecoverOrStrand(
        ArtifactCandidate candidate,
        string requestedRoot,
        string candidatePath,
        string quarantineRoot,
        FileSystemIdentity quarantineIdentity,
        string destinationPath,
        string reason)
    {
        if (!HasIdentity(quarantineRoot, quarantineIdentity) ||
            !TryGetIdentity(destinationPath, out var strandedIdentity, out _) ||
            strandedIdentity is null)
        {
            return new CleanupCandidateResult(candidate, CleanupOutcome.Failed, $"{reason} Cleanup stopped; an object may be stranded at '{destinationPath}'.");
        }

        var originalParent = Path.GetDirectoryName(candidatePath)!;
        var boundaryError = boundaryInspector.Inspect(requestedRoot, originalParent);
        if (boundaryError is not null || boundaryInspector.EntryExists(candidatePath))
        {
            return new CleanupCandidateResult(candidate, CleanupOutcome.Failed, $"{reason} Cleanup stopped; the mismatched object is stranded at '{destinationPath}'.");
        }

        try
        {
            fileSystem.Move(destinationPath, candidatePath, CleanupIdentity.IsDirectory(strandedIdentity));
            if (!HasIdentity(candidatePath, strandedIdentity))
            {
                return new CleanupCandidateResult(candidate, CleanupOutcome.Failed, $"{reason} Recovery identity verification failed; inspect '{candidatePath}' and '{destinationPath}'.");
            }

            var quarantineRemoved = TryRemoveEmptyQuarantine(quarantineRoot, quarantineIdentity, out var cleanupError);
            var cleanupMessage = quarantineRemoved
                ? string.Empty
                : $" Its empty quarantine remains at '{quarantineRoot}': {cleanupError}";
            return new CleanupCandidateResult(candidate, CleanupOutcome.Failed, $"{reason} The mismatched object was restored to its original path and was not deleted.{cleanupMessage}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new CleanupCandidateResult(candidate, CleanupOutcome.Failed, $"{reason} Recovery failed: {exception.Message} The object is stranded at '{destinationPath}'.");
        }
    }

    private bool TryGetIdentity(string path, out FileSystemIdentity? identity, out string? error) =>
        identityProvider.TryGetIdentity(path, out identity, out error);

    private bool HasIdentity(string path, FileSystemIdentity expected) =>
        TryGetIdentity(path, out var current, out _) && current is not null &&
        CleanupIdentity.HasSameStableIdentity(expected, current) &&
        CleanupIdentity.IsDirectory(expected) == CleanupIdentity.IsDirectory(current);

    private bool TryRemoveEmptyQuarantine(string quarantineRoot, FileSystemIdentity? expectedIdentity, out string? error)
    {
        try
        {
            if (expectedIdentity is not null && !HasIdentity(quarantineRoot, expectedIdentity))
            {
                error = "Quarantine identity changed before cleanup.";
                return false;
            }

            fileSystem.DeleteDirectory(quarantineRoot);
            error = null;
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            error = null;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            error = exception.Message;
            return false;
        }
    }
}

internal sealed class CleanupMutationInterruptedException : OperationCanceledException
{
    public CleanupMutationInterruptedException(CleanupCandidateResult result, OperationCanceledException innerException)
        : base("Cleanup was interrupted after candidate mutation began.", innerException, innerException.CancellationToken)
    {
        Result = result;
    }

    public CleanupCandidateResult Result { get; }
}
