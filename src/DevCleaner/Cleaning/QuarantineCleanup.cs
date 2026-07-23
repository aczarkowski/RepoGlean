using DevCleaner.Git;
using DevCleaner.Rules;
using DevCleaner.Scanning;

namespace DevCleaner.Cleaning;

internal sealed class QuarantineCleanup(
    ICleanupFileSystem fileSystem,
    IAtomicFileMover atomicFileMover,
    ICleanupMutationObserver mutationObserver,
    IFileSystemIdentityProvider identityProvider,
    CleanupBoundaryInspector boundaryInspector,
    CleanupAuthorityValidator authorityValidator,
    OwnedTreeInspector treeInspector,
    BoundaryAwareDeleter deleter)
{
    public async Task<CleanupCandidateResult> ExecuteAsync(
        ArtifactCandidate candidate,
        CleanupAuthorityValidation validation,
        RuleCatalog ruleCatalog,
        CancellationToken cancellationToken)
    {
        var requestedRoot = validation.RequestedRoot!;
        var repositoryRoot = validation.RepositoryRoot!;
        var candidatePath = validation.CandidatePath!;
        var repositoryIdentity = validation.RepositoryIdentity!;
        var candidateIdentity = validation.CandidateIdentity!;
        var isDirectory = validation.IsDirectory;
        var quarantineRoot = Path.Combine(repositoryRoot, $"{GitClient.QuarantineDirectoryPrefix}{Guid.NewGuid():N}");
        var destinationPath = Path.Combine(quarantineRoot, "payload");
        FileSystemIdentity? quarantineIdentity = null;
        OwnedTreeSnapshot? recoverySnapshot = null;
        var moved = false;
        try
        {
            fileSystem.CreateDirectory(quarantineRoot);
            if (!TryGetIdentity(quarantineRoot, out quarantineIdentity, out var quarantineError) || quarantineIdentity is null)
            {
                return FailBeforeOwnership(
                    candidate,
                    quarantineRoot,
                    null,
                    quarantineError ?? "Unable to identify the cleanup quarantine directory.",
                    out _);
            }

            if (!CleanupIdentity.IsDirectory(quarantineIdentity) ||
                !CleanupIdentity.IsSameMount(repositoryIdentity, quarantineIdentity) ||
                !CleanupIdentity.IsSameMount(candidateIdentity, quarantineIdentity))
            {
                return FailBeforeOwnership(
                    candidate,
                    quarantineRoot,
                    quarantineIdentity,
                    "Cleanup quarantine is not a safe directory on the validated repository filesystem mount.",
                    out _);
            }

            cancellationToken.ThrowIfCancellationRequested();
            mutationObserver.BeforeQuarantineMove(candidate, quarantineRoot, destinationPath);
            cancellationToken.ThrowIfCancellationRequested();

            var preMoveAuthority = await authorityValidator.RevalidateAsync(
                candidate,
                requestedRoot,
                ruleCatalog,
                candidatePresent: true,
                quarantinePath: null,
                cancellationToken).ConfigureAwait(false);
            if (!preMoveAuthority.IsValid)
            {
                return FailBeforeOwnership(
                    candidate,
                    quarantineRoot,
                    quarantineIdentity,
                    $"Cleanup authority changed at the quarantine move boundary: {preMoveAuthority.Error} The source was not moved or deleted.",
                    out _);
            }

            if (!HasIdentity(quarantineRoot, quarantineIdentity) ||
                !CleanupIdentity.IsSameMount(preMoveAuthority.RepositoryIdentity!, quarantineIdentity) ||
                !CleanupIdentity.HasSameStableIdentity(candidateIdentity, preMoveAuthority.CandidateIdentity!) ||
                isDirectory != preMoveAuthority.IsDirectory)
            {
                return FailBeforeOwnership(
                    candidate,
                    quarantineRoot,
                    quarantineIdentity,
                    "Candidate or quarantine identity, type, or filesystem mount changed at the quarantine move boundary. The source was not moved or deleted.",
                    out _);
            }

            atomicFileMover.MoveNoCopy(candidatePath, destinationPath);
            moved = true;
            mutationObserver.BeforeMovedIdentityCheck(candidate, quarantineRoot, destinationPath);

            string? movedIdentityError = null;
            if (!HasIdentity(quarantineRoot, quarantineIdentity) ||
                !TryGetIdentity(destinationPath, out var movedIdentity, out movedIdentityError) ||
                movedIdentity is null ||
                !IsExpectedPayload(movedIdentity, candidateIdentity, isDirectory, repositoryIdentity))
            {
                return RecoverOrStrand(
                    candidate,
                    requestedRoot,
                    repositoryRoot,
                    candidatePath,
                    quarantineRoot,
                    quarantineIdentity,
                    destinationPath,
                    candidateIdentity,
                    isDirectory,
                    movedIdentityError ?? "Moved object identity, type, or mount does not match the validated candidate.");
            }

            var postMoveAuthority = await authorityValidator.RevalidateAsync(
                candidate,
                requestedRoot,
                ruleCatalog,
                candidatePresent: false,
                quarantineRoot,
                cancellationToken).ConfigureAwait(false);
            if (!postMoveAuthority.IsValid)
            {
                return RecoverOrStrand(
                    candidate,
                    requestedRoot,
                    repositoryRoot,
                    candidatePath,
                    quarantineRoot,
                    quarantineIdentity,
                    destinationPath,
                    candidateIdentity,
                    isDirectory,
                    $"Cleanup authority changed after quarantine ownership: {postMoveAuthority.Error}");
            }

            if (!treeInspector.TryCapture(
                    destinationPath,
                    repositoryIdentity,
                    cancellationToken,
                    out var ownedSnapshot,
                    out var snapshotError) ||
                ownedSnapshot is null ||
                !SnapshotOwnsExpectedPayload(ownedSnapshot, candidateIdentity, isDirectory))
            {
                return RecoverOrStrand(
                    candidate,
                    requestedRoot,
                    repositoryRoot,
                    candidatePath,
                    quarantineRoot,
                    quarantineIdentity,
                    destinationPath,
                    candidateIdentity,
                    isDirectory,
                    snapshotError ?? "Owned descendant snapshot does not match the validated payload.");
            }

            recoverySnapshot = ownedSnapshot;

            mutationObserver.BeforeRecursiveDelete(candidate, quarantineRoot, destinationPath);

            var finalAuthority = await authorityValidator.RevalidateAsync(
                candidate,
                requestedRoot,
                ruleCatalog,
                candidatePresent: false,
                quarantineRoot,
                cancellationToken).ConfigureAwait(false);
            if (!finalAuthority.IsValid)
            {
                return RecoverOrStrand(
                    candidate,
                    requestedRoot,
                    repositoryRoot,
                    candidatePath,
                    quarantineRoot,
                    quarantineIdentity,
                    destinationPath,
                    candidateIdentity,
                    isDirectory,
                    $"Cleanup authority changed at the final deletion boundary: {finalAuthority.Error}");
            }

            if (!treeInspector.TryCapture(
                    destinationPath,
                    repositoryIdentity,
                    cancellationToken,
                    out var finalSnapshot,
                    out var finalSnapshotError) ||
                finalSnapshot is null)
            {
                return RecoverOrStrand(
                    candidate,
                    requestedRoot,
                    repositoryRoot,
                    candidatePath,
                    quarantineRoot,
                    quarantineIdentity,
                    destinationPath,
                    candidateIdentity,
                    isDirectory,
                    finalSnapshotError ?? "Unable to capture the final owned descendant snapshot.");
            }

            if (!ownedSnapshot.HasSameEntries(finalSnapshot))
            {
                return RecoverOrStrand(
                    candidate,
                    requestedRoot,
                    repositoryRoot,
                    candidatePath,
                    quarantineRoot,
                    quarantineIdentity,
                    destinationPath,
                    candidateIdentity,
                    isDirectory,
                    "Owned descendant snapshot changed at the final deletion boundary.");
            }

            var immediateAuthority = await authorityValidator.RevalidateAsync(
                candidate,
                requestedRoot,
                ruleCatalog,
                candidatePresent: false,
                quarantineRoot,
                cancellationToken).ConfigureAwait(false);
            if (!immediateAuthority.IsValid ||
                !HasIdentity(quarantineRoot, quarantineIdentity) ||
                !HasExpectedPayload(destinationPath, candidateIdentity, isDirectory, repositoryIdentity))
            {
                return RecoverOrStrand(
                    candidate,
                    requestedRoot,
                    repositoryRoot,
                    candidatePath,
                    quarantineRoot,
                    quarantineIdentity,
                    destinationPath,
                    candidateIdentity,
                    isDirectory,
                    immediateAuthority.Error ?? "Quarantine ownership changed immediately before permanent deletion.");
            }

            deleter.Delete(destinationPath, finalSnapshot, repositoryIdentity, cancellationToken);
            moved = false;
            if (!TryRemoveEmptyQuarantine(quarantineRoot, quarantineIdentity, out var cleanupError))
            {
                return new CleanupCandidateResult(
                    candidate,
                    CleanupOutcome.Failed,
                    $"Candidate was permanently deleted, but its empty quarantine could not be removed and remains at '{quarantineRoot}': {cleanupError}",
                    DeletionCompleted: true);
            }

            return new CleanupCandidateResult(
                candidate,
                CleanupOutcome.Deleted,
                "Deleted after repository-bound quarantine ownership, descendant snapshot validation, and boundary-aware deletion.",
                DeletionCompleted: true);
        }
        catch (OperationCanceledException exception) when (moved && quarantineIdentity is not null)
        {
            var result = RecoverOrStrand(
                candidate,
                requestedRoot,
                repositoryRoot,
                candidatePath,
                quarantineRoot,
                quarantineIdentity,
                destinationPath,
                candidateIdentity,
                isDirectory,
                "Cleanup was interrupted after the candidate entered quarantine and may have been partially mutated.",
                recoverySnapshot);
            throw new CleanupMutationInterruptedException(result, exception);
        }
        catch (OperationCanceledException exception)
        {
            var result = FailBeforeOwnership(
                candidate,
                quarantineRoot,
                quarantineIdentity,
                "Cleanup was interrupted before the candidate entered quarantine.",
                out var quarantineRemoved);
            if (quarantineRemoved) throw;
            throw new CleanupMutationInterruptedException(result, exception);
        }
        catch (Exception exception) when (moved && quarantineIdentity is not null && exception is
            IOException or
            UnauthorizedAccessException or
            System.Security.SecurityException)
        {
            return RecoverOrStrand(
                candidate,
                requestedRoot,
                repositoryRoot,
                candidatePath,
                quarantineRoot,
                quarantineIdentity,
                destinationPath,
                candidateIdentity,
                isDirectory,
                $"Boundary-aware permanent deletion failed after quarantine ownership: {exception.Message}",
                recoverySnapshot);
        }
        catch (Exception exception) when (exception is
            IOException or
            UnauthorizedAccessException or
            System.Security.SecurityException)
        {
            return FailBeforeOwnership(
                candidate,
                quarantineRoot,
                quarantineIdentity,
                $"Cleanup ownership preparation or atomic move failed: {exception.Message}",
                out _);
        }
    }

    private CleanupCandidateResult FailBeforeOwnership(
        ArtifactCandidate candidate,
        string quarantineRoot,
        FileSystemIdentity? quarantineIdentity,
        string reason,
        out bool quarantineRemoved)
    {
        quarantineRemoved = TryRemoveEmptyQuarantine(quarantineRoot, quarantineIdentity, out var cleanupError);
        var cleanupMessage = quarantineRemoved
            ? string.Empty
            : $" Empty quarantine cleanup failed; it may remain at '{quarantineRoot}': {cleanupError}";
        return new CleanupCandidateResult(candidate, CleanupOutcome.Failed, reason + cleanupMessage);
    }

    private CleanupCandidateResult RecoverOrStrand(
        ArtifactCandidate candidate,
        string requestedRoot,
        string repositoryRoot,
        string candidatePath,
        string quarantineRoot,
        FileSystemIdentity quarantineIdentity,
        string destinationPath,
        FileSystemIdentity expectedPayloadIdentity,
        bool expectedDirectory,
        string reason,
        OwnedTreeSnapshot? expectedSnapshot = null)
    {
        try
        {
            if (!HasIdentity(quarantineRoot, quarantineIdentity) ||
                !HasExpectedPayload(destinationPath, expectedPayloadIdentity, expectedDirectory, candidate.RepositoryIdentity))
            {
                if (TryLocateOwnedPayload(
                        requestedRoot,
                        repositoryRoot,
                        Path.GetFileName(quarantineRoot),
                        quarantineIdentity,
                        expectedPayloadIdentity,
                        expectedDirectory,
                        candidate.RepositoryIdentity,
                        out var relocatedPayload))
                {
                    return new CleanupCandidateResult(
                        candidate,
                        CleanupOutcome.Failed,
                        $"{reason} Cleanup stopped after the repository-local quarantine moved; the exact owned payload is stranded at '{relocatedPayload}'.");
                }

                return new CleanupCandidateResult(
                    candidate,
                    CleanupOutcome.Failed,
                    $"{reason} Cleanup stopped; the expected owned payload is missing, mismatched, or uncertain and an object may be stranded at '{destinationPath}'.");
            }

            if (expectedSnapshot is not null &&
                !HasExpectedSnapshot(destinationPath, candidate.RepositoryIdentity, expectedSnapshot, out var snapshotError))
            {
                return new CleanupCandidateResult(
                    candidate,
                    CleanupOutcome.Failed,
                    $"{reason} Cleanup stopped after partial or uncertain permanent mutation; the remaining owned payload is stranded at '{destinationPath}': {snapshotError}");
            }

            if (!HasIdentity(repositoryRoot, candidate.RepositoryIdentity) ||
                boundaryInspector.Inspect(requestedRoot, repositoryRoot) is not null)
            {
                return new CleanupCandidateResult(
                    candidate,
                    CleanupOutcome.Failed,
                    $"{reason} Cleanup stopped because the repository ownership boundary changed; the owned payload is stranded at '{destinationPath}'.");
            }

            var originalParent = Path.GetDirectoryName(candidatePath)!;
            var boundaryError = boundaryInspector.Inspect(repositoryRoot, originalParent);
            if (boundaryError is not null || boundaryInspector.EntryExists(candidatePath))
            {
                return new CleanupCandidateResult(
                    candidate,
                    CleanupOutcome.Failed,
                    $"{reason} Cleanup stopped; the owned payload is stranded at '{destinationPath}'.");
            }

            if (!HasExpectedPayload(destinationPath, expectedPayloadIdentity, expectedDirectory, candidate.RepositoryIdentity))
            {
                return new CleanupCandidateResult(
                    candidate,
                    CleanupOutcome.Failed,
                    $"{reason} Recovery refused a missing, replaced, or uncertain payload; an object may remain at '{destinationPath}'.");
            }

            if (expectedSnapshot is not null &&
                !HasExpectedSnapshot(destinationPath, candidate.RepositoryIdentity, expectedSnapshot, out snapshotError))
            {
                return new CleanupCandidateResult(
                    candidate,
                    CleanupOutcome.Failed,
                    $"{reason} Recovery refused a partial or changed payload; the remaining owned payload is stranded at '{destinationPath}': {snapshotError}");
            }

            try
            {
                mutationObserver.BeforeRecoveryMove(candidate, quarantineRoot, destinationPath, candidatePath);
                if (!HasExpectedPayload(destinationPath, expectedPayloadIdentity, expectedDirectory, candidate.RepositoryIdentity))
                {
                    return new CleanupCandidateResult(
                        candidate,
                        CleanupOutcome.Failed,
                        $"{reason} Recovery refused a payload whose identity, type, or mount changed immediately before the reverse move; an object may remain at '{destinationPath}'.");
                }

                atomicFileMover.MoveNoCopy(destinationPath, candidatePath);
                if (!HasExpectedPayload(candidatePath, expectedPayloadIdentity, expectedDirectory, candidate.RepositoryIdentity))
                {
                    return new CleanupCandidateResult(
                        candidate,
                        CleanupOutcome.Failed,
                        $"{reason} Recovery identity verification failed; inspect '{candidatePath}' and '{destinationPath}'.");
                }

                var quarantineRemoved = TryRemoveEmptyQuarantine(quarantineRoot, quarantineIdentity, out var cleanupError);
                var cleanupMessage = quarantineRemoved
                    ? string.Empty
                    : $" Its empty quarantine remains at '{quarantineRoot}': {cleanupError}";
                return new CleanupCandidateResult(
                    candidate,
                    CleanupOutcome.Failed,
                    $"{reason} The exact owned payload was restored to its original path and was not deleted.{cleanupMessage}");
            }
            catch (Exception exception) when (exception is
                IOException or
                UnauthorizedAccessException or
                System.Security.SecurityException)
            {
                return new CleanupCandidateResult(
                    candidate,
                    CleanupOutcome.Failed,
                    $"{reason} Recovery failed: {exception.Message} The owned payload is stranded at '{destinationPath}'.");
            }
        }
        catch (Exception exception) when (exception is
            IOException or
            UnauthorizedAccessException or
            System.Security.SecurityException)
        {
            return new CleanupCandidateResult(
                candidate,
                CleanupOutcome.Failed,
                $"{reason} Recovery inspection failed: {exception.Message} An object may remain at '{destinationPath}'.");
        }
    }

    private bool SnapshotOwnsExpectedPayload(
        OwnedTreeSnapshot snapshot,
        FileSystemIdentity expectedIdentity,
        bool expectedDirectory)
    {
        var root = snapshot.Entries.FirstOrDefault(entry => entry.RelativePath == ".");
        return root is not null &&
               CleanupIdentity.HasSameStableIdentity(root.Identity, expectedIdentity) &&
               (root.Type == OwnedTreeEntryType.Directory) == expectedDirectory;
    }

    private bool TryLocateOwnedPayload(
        string requestedRoot,
        string repositoryRoot,
        string quarantineName,
        FileSystemIdentity expectedQuarantineIdentity,
        FileSystemIdentity expectedPayloadIdentity,
        bool expectedDirectory,
        FileSystemIdentity expectedMount,
        out string? payloadPath)
    {
        var pending = new Stack<string>();
        var visited = new HashSet<string>(PathComparer);
        foreach (var searchRoot in new[] { requestedRoot, Path.GetDirectoryName(repositoryRoot) })
        {
            if (!string.IsNullOrWhiteSpace(searchRoot)) pending.Push(Path.GetFullPath(searchRoot));
        }

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (!visited.Add(current)) continue;
            try
            {
                var attributes = fileSystem.GetAttributes(current);
                if ((attributes & FileAttributes.Directory) == 0 ||
                    (attributes & FileAttributes.ReparsePoint) != 0 ||
                    !TryGetIdentity(current, out var currentIdentity, out _) ||
                    currentIdentity is null ||
                    !CleanupIdentity.IsSameMount(expectedMount, currentIdentity))
                {
                    continue;
                }

                if (string.Equals(Path.GetFileName(current), quarantineName, StringComparison.OrdinalIgnoreCase) &&
                    CleanupIdentity.HasSameStableIdentity(expectedQuarantineIdentity, currentIdentity))
                {
                    var candidatePayload = Path.Combine(current, "payload");
                    if (HasExpectedPayload(
                            candidatePayload,
                            expectedPayloadIdentity,
                            expectedDirectory,
                            expectedMount))
                    {
                        payloadPath = candidatePayload;
                        return true;
                    }

                    continue;
                }

                var currentName = Path.GetFileName(current);
                if (string.Equals(currentName, ".git", StringComparison.OrdinalIgnoreCase) ||
                    currentName.StartsWith(GitClient.QuarantineDirectoryPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (var entry in fileSystem.GetFileSystemEntries(current))
                {
                    try
                    {
                        var entryAttributes = fileSystem.GetAttributes(entry);
                        if ((entryAttributes & FileAttributes.Directory) != 0 &&
                            (entryAttributes & FileAttributes.ReparsePoint) == 0)
                        {
                            pending.Push(entry);
                        }
                    }
                    catch (Exception exception) when (exception is
                        IOException or
                        UnauthorizedAccessException or
                        System.Security.SecurityException)
                    {
                    }
                }
            }
            catch (Exception exception) when (exception is
                IOException or
                UnauthorizedAccessException or
                System.Security.SecurityException)
            {
            }
        }

        payloadPath = null;
        return false;
    }

    private bool HasExpectedSnapshot(
        string path,
        FileSystemIdentity expectedMount,
        OwnedTreeSnapshot expectedSnapshot,
        out string? error)
    {
        if (!treeInspector.TryCapture(
                path,
                expectedMount,
                CancellationToken.None,
                out var currentSnapshot,
                out error) ||
            currentSnapshot is null)
        {
            return false;
        }

        if (!expectedSnapshot.HasSameEntries(currentSnapshot))
        {
            error = "The owned descendant snapshot no longer matches the pre-deletion payload.";
            return false;
        }

        error = null;
        return true;
    }

    private bool HasExpectedPayload(
        string path,
        FileSystemIdentity expectedIdentity,
        bool expectedDirectory,
        FileSystemIdentity expectedMount) =>
        TryGetIdentity(path, out var current, out _) &&
        current is not null &&
        IsExpectedPayload(current, expectedIdentity, expectedDirectory, expectedMount);

    private static bool IsExpectedPayload(
        FileSystemIdentity current,
        FileSystemIdentity expectedIdentity,
        bool expectedDirectory,
        FileSystemIdentity expectedMount) =>
        CleanupIdentity.HasSameStableIdentity(expectedIdentity, current) &&
        expectedDirectory == CleanupIdentity.IsDirectory(current) &&
        CleanupIdentity.IsSameMount(expectedMount, current);

    private bool TryGetIdentity(string path, out FileSystemIdentity? identity, out string? error) =>
        identityProvider.TryGetIdentity(path, out identity, out error);

    private bool HasIdentity(string path, FileSystemIdentity expected) =>
        TryGetIdentity(path, out var current, out _) &&
        current is not null &&
        CleanupIdentity.HasSameStableIdentity(expected, current) &&
        CleanupIdentity.IsDirectory(expected) == CleanupIdentity.IsDirectory(current);

    private bool TryRemoveEmptyQuarantine(
        string quarantineRoot,
        FileSystemIdentity? expectedIdentity,
        out string? error)
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
        catch (Exception exception) when (exception is
            IOException or
            UnauthorizedAccessException or
            System.Security.SecurityException)
        {
            error = exception.Message;
            return false;
        }
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}

internal sealed class CleanupMutationInterruptedException : OperationCanceledException
{
    public CleanupMutationInterruptedException(
        CleanupCandidateResult result,
        OperationCanceledException innerException)
        : base(
            "Cleanup was interrupted after candidate mutation began.",
            innerException,
            innerException.CancellationToken)
    {
        Result = result;
    }

    public CleanupCandidateResult Result { get; }
}
