using RepoGlean.Git;
using RepoGlean.Progress;
using RepoGlean.Rules;
using RepoGlean.Scanning;

namespace RepoGlean.Auditing;

public sealed class RepositoryAuditor
{
    private readonly GitClient git;
    private readonly ISecureAuditFileSystem fileSystem;
    private readonly IOperationProgress progress;
    private readonly Action<SecureAuditCheckpoint, string>? checkpoint;

    public RepositoryAuditor(GitClient git)
        : this(git, new SecureAuditFileSystem(), NullOperationProgress.Instance, checkpoint: null)
    {
    }

    internal RepositoryAuditor(GitClient git, IOperationProgress progress)
        : this(git, new SecureAuditFileSystem(), progress, checkpoint: null)
    {
    }

    internal RepositoryAuditor(
        GitClient git,
        IVolumeBoundary volumeBoundary,
        IFileTimestampProvider timestampProvider,
        IOperationProgress progress)
        : this(git, new SecureAuditFileSystem(volumeBoundary, timestampProvider), progress, checkpoint: null)
    {
    }

    internal RepositoryAuditor(
        GitClient git,
        ISecureAuditFileSystem fileSystem,
        IOperationProgress progress,
        Action<SecureAuditCheckpoint, string>? checkpoint = null)
    {
        this.git = git ?? throw new ArgumentNullException(nameof(git));
        this.fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        this.progress = progress ?? throw new ArgumentNullException(nameof(progress));
        this.checkpoint = checkpoint;
    }

    public Task<AuditResult> AuditAsync(
        IReadOnlyList<string> repositoryRoots,
        RuleCatalog ruleCatalog,
        AuditOptions? options = null,
        CancellationToken cancellationToken = default) =>
        AuditCoreAsync(repositoryRoots, ruleCatalog, options, cancellationToken);

    private async Task<AuditResult> AuditCoreAsync(
        IReadOnlyList<string> repositoryRoots,
        RuleCatalog ruleCatalog,
        AuditOptions? options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repositoryRoots);
        ArgumentNullException.ThrowIfNull(ruleCatalog);
        options ??= new AuditOptions([], [], AuditOptions.DefaultMinimumBytes);
        if (options.MinimumBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The audit minimum size cannot be negative.");
        }

        if (repositoryRoots.Count > 0) cancellationToken.ThrowIfCancellationRequested();
        var selectedRepositoryRoots = repositoryRoots
            .Select(SecureAuditFileSystem.NormalizeRootPath)
            .Distinct(RepositoryPathPolicy.PathComparer)
            .Where(repositoryRoot => MatchesRepositoryFilter(repositoryRoot, options.RepositoryFilters))
            .ToArray();
        var repositories = new List<RepositoryAuditResult>();
        var allWarnings = new List<OperationWarning>();
        long completedRepositoryCount = 0;
        long cumulativeFindingCount = 0;
        long cumulativeEstimatedBytes = 0;

        for (var index = 0; index < selectedRepositoryRoots.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var repositoryRoot = selectedRepositoryRoots[index];
            var current = index + 1;
            ReportProgress(new OperationProgressEvent(
                ProgressEventKind.RepositoryScanStarted,
                ProgressOperation.Audit,
                Path: repositoryRoot,
                Current: current,
                Total: selectedRepositoryRoots.Length,
                RepositoryCount: completedRepositoryCount,
                FindingCount: cumulativeFindingCount,
                EstimatedBytes: cumulativeEstimatedBytes,
                WarningCount: allWarnings.Count));
            IReadOnlyList<string> visiblePaths;
            try
            {
                if (!await git.IsWorkingTreeAsync(repositoryRoot, cancellationToken).ConfigureAwait(false))
                {
                    AddWarning(allWarnings, repositoryRoot, "Path is not a Git working tree.");
                    ReportRepositoryAuditCompleted(
                        repositoryRoot,
                        current,
                        selectedRepositoryRoots.Length,
                        completedRepositoryCount,
                        0,
                        0,
                        cumulativeFindingCount,
                        cumulativeEstimatedBytes,
                        allWarnings.Count);
                    continue;
                }

                visiblePaths = await git.ListVisibleFilesAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
            }
            catch (GitCommandException exception)
            {
                AddWarning(allWarnings, repositoryRoot, exception.Message);
                ReportRepositoryAuditCompleted(
                    repositoryRoot,
                    current,
                    selectedRepositoryRoots.Length,
                    completedRepositoryCount,
                    0,
                    0,
                    cumulativeFindingCount,
                    cumulativeEstimatedBytes,
                    allWarnings.Count);
                continue;
            }

            var repositoryWarnings = new List<OperationWarning>();
            var activeRules = ruleCatalog.Rules
                .Where(rule => rule.IsActiveFor(visiblePaths))
                .ToArray();
            var visiblePathSet = RepositoryPathPolicy.CreateVisiblePathSet(visiblePaths);
            if (!fileSystem.TryOpenRoot(repositoryRoot, out var secureRoot, out var repositoryOpenError) ||
                secureRoot is null)
            {
                AddWarning(
                    repositoryWarnings,
                    repositoryRoot,
                    repositoryOpenError ?? "Unable to securely open the audit repository root.");
                AddRepositoryResult(repositoryRoot, [], repositoryWarnings, repositories, allWarnings);
                completedRepositoryCount = FileTreeAnalyzer.SaturatingAdd(completedRepositoryCount, 1);
                ReportRepositoryAuditCompleted(
                    repositoryRoot,
                    current,
                    selectedRepositoryRoots.Length,
                    completedRepositoryCount,
                    0,
                    0,
                    cumulativeFindingCount,
                    cumulativeEstimatedBytes,
                    allWarnings.Count);
                continue;
            }

            AuditAggregate aggregate;
            using (secureRoot)
            {
                aggregate = await AuditDirectoryContentsAsync(
                    repositoryRoot,
                    secureRoot,
                    visiblePathSet,
                    activeRules,
                    options,
                    secureRoot.MountIdentity,
                    repositoryWarnings,
                    cancellationToken).ConfigureAwait(false);
                if (!TryConfirmUnchanged(secureRoot, repositoryWarnings)) aggregate = AuditAggregate.Empty;
            }
            var findings = aggregate.Findings
                .Where(finding => finding.EstimatedBytes >= options.MinimumBytes)
                .OrderByDescending(finding => finding.EstimatedBytes)
                .ThenBy(finding => finding.RelativePath, StringComparer.Ordinal)
                .ToArray();
            AddRepositoryResult(repositoryRoot, findings, repositoryWarnings, repositories, allWarnings);
            long currentEstimatedBytes = 0;
            foreach (var finding in findings)
            {
                currentEstimatedBytes = FileTreeAnalyzer.SaturatingAdd(
                    currentEstimatedBytes,
                    finding.EstimatedBytes);
            }

            completedRepositoryCount = FileTreeAnalyzer.SaturatingAdd(completedRepositoryCount, 1);
            cumulativeFindingCount = FileTreeAnalyzer.SaturatingAdd(cumulativeFindingCount, findings.LongLength);
            cumulativeEstimatedBytes = FileTreeAnalyzer.SaturatingAdd(
                cumulativeEstimatedBytes,
                currentEstimatedBytes);
            ReportRepositoryAuditCompleted(
                repositoryRoot,
                current,
                selectedRepositoryRoots.Length,
                completedRepositoryCount,
                findings.LongLength,
                currentEstimatedBytes,
                cumulativeFindingCount,
                cumulativeEstimatedBytes,
                allWarnings.Count);
        }

        repositories.Sort((left, right) =>
        {
            var byBytes = right.EstimatedBytes.CompareTo(left.EstimatedBytes);
            return byBytes != 0
                ? byBytes
                : RepositoryPathPolicy.PathComparer.Compare(left.RepositoryRoot, right.RepositoryRoot);
        });

        long totalFiles = 0;
        long totalBytes = 0;
        foreach (var repository in repositories)
        {
            totalFiles = FileTreeAnalyzer.SaturatingAdd(totalFiles, repository.FileCount);
            totalBytes = FileTreeAnalyzer.SaturatingAdd(totalBytes, repository.EstimatedBytes);
        }

        return new AuditResult(
            Array.AsReadOnly(repositories.ToArray()),
            totalFiles,
            totalBytes,
            Array.AsReadOnly(allWarnings.ToArray()));
    }

    private async Task<AuditAggregate> AuditDirectoryContentsAsync(
        string repositoryRoot,
        ISecureAuditEntry directory,
        IReadOnlySet<string> visiblePaths,
        IReadOnlyList<ArtifactRule> activeRules,
        AuditOptions options,
        FileSystemMountIdentity repositoryMount,
        List<OperationWarning> warnings,
        CancellationToken cancellationToken)
    {
        if (!directory.TryEnumerate(cancellationToken, out var secureEntries, out var enumerationError))
        {
            AddWarning(
                warnings,
                directory.AbsolutePath,
                enumerationError ?? "Unable to securely enumerate the audit directory.");
            return AuditAggregate.Empty;
        }

        var orderedEntries = secureEntries
            .OrderBy(entry => entry.Name, RepositoryPathPolicy.PathComparer)
            .ToArray();
        try
        {
            var isRepositoryRoot = RepositoryPathPolicy.PathComparer.Equals(directory.AbsolutePath, repositoryRoot);
            if (!isRepositoryRoot && orderedEntries.Any(entry =>
                    string.Equals(entry.Name, ".git", StringComparison.OrdinalIgnoreCase)))
            {
                AddWarning(warnings, directory.AbsolutePath, "Skipped nested repository boundary.");
                return AuditAggregate.Empty;
            }

            var entries = new List<AuditEntry>(orderedEntries.Length);
            foreach (var secureEntry in orderedEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.Equals(secureEntry.Name, ".git", StringComparison.OrdinalIgnoreCase)) continue;
                var path = secureEntry.AbsolutePath;
                var relativePath = RepositoryPathPolicy.NormalizeRelativePath(Path.GetRelativePath(repositoryRoot, path));
                if (RepositoryPathPolicy.IsExcluded(path, relativePath, options.Exclusions)) continue;
                if (RepositoryPathPolicy.IsReservedRootQuarantine(relativePath))
                {
                    AddWarning(
                        warnings,
                        path,
                        "Skipped reserved RepoGlean quarantine; inspect or remove the stranded payload manually.");
                    continue;
                }

                // Git-visible entries are carved before link warnings. A tracked or otherwise visible
                // symbolic link is ordinary repository content, not a partial audit failure.
                if (visiblePaths.Contains(relativePath)) continue;
                if (activeRules.Any(rule => rule.Matches(relativePath))) continue;

                if (secureEntry.InspectionError is not null)
                {
                    AddWarning(warnings, path, secureEntry.InspectionError);
                    continue;
                }

                if (secureEntry.Kind == FileSystemEntryKind.Link)
                {
                    AddWarning(warnings, path, "Skipped audit filesystem link, junction, or reparse point.");
                    continue;
                }

                if (secureEntry.Kind == FileSystemEntryKind.Other)
                {
                    AddWarning(warnings, path, "Skipped non-regular audit filesystem entry.");
                    continue;
                }

                if (secureEntry.MountIdentity != repositoryMount)
                {
                    AddWarning(warnings, path, "Skipped path on a different filesystem mount or volume.");
                    continue;
                }

                entries.Add(new AuditEntry(secureEntry, relativePath));
            }

            var matches = await GetIgnoreMatchesAsync(
                repositoryRoot,
                entries,
                warnings,
                cancellationToken).ConfigureAwait(false);
            var aggregate = new AuditAggregate();
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!matches.TryGetValue(entry.RelativePath, out var match)) continue;
                if (!TryRevalidateEntry(entry, repositoryMount, warnings, out var revalidated) ||
                    revalidated is null)
                {
                    continue;
                }

                using (revalidated)
                {
                    checkpoint?.Invoke(
                        revalidated.Kind == FileSystemEntryKind.Directory
                            ? SecureAuditCheckpoint.BeforeDirectoryEnumeration
                            : SecureAuditCheckpoint.BeforeFileMeasurement,
                        revalidated.AbsolutePath);
                    var child = revalidated.Kind == FileSystemEntryKind.Directory
                        ? await AuditDirectoryAsync(
                            repositoryRoot,
                            entry.RelativePath,
                            revalidated,
                            match,
                            visiblePaths,
                            activeRules,
                            options,
                            repositoryMount,
                            warnings,
                            cancellationToken).ConfigureAwait(false)
                        : AuditFile(repositoryRoot, entry.RelativePath, revalidated, match, warnings);
                    aggregate.Absorb(child);
                }
            }

            return aggregate;
        }
        finally
        {
            foreach (var secureEntry in secureEntries) secureEntry.Dispose();
        }
    }

    private bool TryRevalidateEntry(
        AuditEntry entry,
        FileSystemMountIdentity repositoryMount,
        List<OperationWarning> warnings,
        out ISecureAuditEntry? revalidated)
    {
        if (!entry.SecureEntry.TryReopen(out revalidated, out var reopenError) || revalidated is null)
        {
            AddWarning(
                warnings,
                entry.SecureEntry.AbsolutePath,
                reopenError ?? "Unable to securely revalidate the audit entry.");
            return false;
        }

        if (revalidated.Kind == FileSystemEntryKind.Link)
        {
            AddWarning(warnings, entry.SecureEntry.AbsolutePath, "Skipped audit filesystem link, junction, or reparse point.");
            revalidated.Dispose();
            revalidated = null;
            return false;
        }

        if (revalidated.Kind == FileSystemEntryKind.Other)
        {
            AddWarning(warnings, entry.SecureEntry.AbsolutePath, "Skipped non-regular audit filesystem entry.");
            revalidated.Dispose();
            revalidated = null;
            return false;
        }

        if (revalidated.Identity != entry.SecureEntry.Identity)
        {
            AddWarning(warnings, entry.SecureEntry.AbsolutePath, "Skipped audit entry that changed identity or type during classification.");
            revalidated.Dispose();
            revalidated = null;
            return false;
        }

        if (revalidated.MountIdentity != repositoryMount)
        {
            AddWarning(warnings, entry.SecureEntry.AbsolutePath, "Skipped path on a different filesystem mount or volume.");
            revalidated.Dispose();
            revalidated = null;
            return false;
        }

        return true;
    }

    private async Task<AuditAggregate> AuditDirectoryAsync(
        string repositoryRoot,
        string relativePath,
        ISecureAuditEntry entry,
        GitIgnoreMatch match,
        IReadOnlySet<string> visiblePaths,
        IReadOnlyList<ArtifactRule> activeRules,
        AuditOptions options,
        FileSystemMountIdentity repositoryMount,
        List<OperationWarning> warnings,
        CancellationToken cancellationToken)
    {
        var aggregate = await AuditDirectoryContentsAsync(
            repositoryRoot,
            entry,
            visiblePaths,
            activeRules,
            options,
            repositoryMount,
            warnings,
            cancellationToken).ConfigureAwait(false);
        if (!TryConfirmUnchanged(entry, warnings)) return AuditAggregate.Empty;
        if (aggregate.FileCount == 0) return AuditAggregate.Empty;

        aggregate.ObserveTimestamp(entry.LastWriteTimeUtc);
        if (!match.IsIgnored) return aggregate;

        aggregate.ReplaceFindings(new AuditFinding(
            repositoryRoot,
            Path.GetFullPath(entry.AbsolutePath),
            relativePath,
            aggregate.FileCount,
            aggregate.EstimatedBytes,
            aggregate.TimestampUnavailable ? null : aggregate.NewestWriteTimeUtc,
            match));
        return aggregate;
    }

    private AuditAggregate AuditFile(
        string repositoryRoot,
        string relativePath,
        ISecureAuditEntry entry,
        GitIgnoreMatch match,
        List<OperationWarning> warnings)
    {
        if (!match.IsIgnored) return AuditAggregate.Empty;
        var length = entry.Length;
        if (!TryConfirmUnchanged(entry, warnings)) return AuditAggregate.Empty;

        var aggregate = new AuditAggregate
        {
            FileCount = 1,
            EstimatedBytes = length,
        };
        aggregate.ObserveTimestamp(entry.LastWriteTimeUtc);
        aggregate.ReplaceFindings(new AuditFinding(
            repositoryRoot,
            Path.GetFullPath(entry.AbsolutePath),
            relativePath,
            1,
            length,
            aggregate.TimestampUnavailable ? null : aggregate.NewestWriteTimeUtc,
            match));
        return aggregate;
    }

    private bool TryConfirmUnchanged(ISecureAuditEntry entry, List<OperationWarning> warnings)
    {
        if (!entry.TryReopen(out var current, out var reopenError) || current is null)
        {
            AddWarning(
                warnings,
                entry.AbsolutePath,
                reopenError ?? "Unable to securely confirm the audit entry after inspection.");
            return false;
        }

        using (current)
        {
            if (current.Identity == entry.Identity && current.MountIdentity == entry.MountIdentity) return true;
        }

        AddWarning(warnings, entry.AbsolutePath, "Skipped audit entry that changed identity or type during inspection.");
        return false;
    }

    private async Task<Dictionary<string, GitIgnoreMatch>> GetIgnoreMatchesAsync(
        string repositoryRoot,
        IReadOnlyList<AuditEntry> entries,
        List<OperationWarning> warnings,
        CancellationToken cancellationToken)
    {
        var matches = new Dictionary<string, GitIgnoreMatch>(StringComparer.Ordinal);
        foreach (var batch in entries.Chunk(GitClient.MaximumCheckIgnoreBatchSize))
        {
            await ResolveIgnoreBatchAsync(
                repositoryRoot,
                batch,
                matches,
                warnings,
                cancellationToken).ConfigureAwait(false);
        }

        return matches;
    }

    private async Task ResolveIgnoreBatchAsync(
        string repositoryRoot,
        IReadOnlyList<AuditEntry> batch,
        Dictionary<string, GitIgnoreMatch> matches,
        List<OperationWarning> warnings,
        CancellationToken cancellationToken)
    {
        if (batch.Count == 0) return;
        try
        {
            var resolved = await git.GetIgnoreMatchesWithoutIndexAsync(
                repositoryRoot,
                batch.Select(entry => entry.RelativePath).ToArray(),
                cancellationToken).ConfigureAwait(false);
            foreach (var pair in resolved) matches.Add(pair.Key, pair.Value);
        }
        catch (GitCommandException) when (batch.Count > 1)
        {
            var midpoint = batch.Count / 2;
            await ResolveIgnoreBatchAsync(
                repositoryRoot,
                batch.Take(midpoint).ToArray(),
                matches,
                warnings,
                cancellationToken).ConfigureAwait(false);
            await ResolveIgnoreBatchAsync(
                repositoryRoot,
                batch.Skip(midpoint).ToArray(),
                matches,
                warnings,
                cancellationToken).ConfigureAwait(false);
        }
        catch (GitCommandException exception)
        {
            AddWarning(warnings, batch[0].SecureEntry.AbsolutePath, exception.Message);
        }
    }

    private static bool MatchesRepositoryFilter(string repositoryRoot, IReadOnlyList<string> filters)
    {
        if (filters.Count == 0) return true;
        var name = Path.GetFileName(repositoryRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return filters.Any(filter =>
            string.Equals(filter, name, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                SecureAuditFileSystem.NormalizeRootPath(filter),
                repositoryRoot,
                RepositoryPathPolicy.PathComparison));
    }

    private static void AddRepositoryResult(
        string repositoryRoot,
        IReadOnlyList<AuditFinding> findings,
        List<OperationWarning> repositoryWarnings,
        List<RepositoryAuditResult> repositories,
        List<OperationWarning> allWarnings)
    {
        long fileCount = 0;
        long estimatedBytes = 0;
        foreach (var finding in findings)
        {
            fileCount = FileTreeAnalyzer.SaturatingAdd(fileCount, finding.FileCount);
            estimatedBytes = FileTreeAnalyzer.SaturatingAdd(estimatedBytes, finding.EstimatedBytes);
        }

        var frozenWarnings = Array.AsReadOnly(repositoryWarnings.ToArray());
        repositories.Add(new RepositoryAuditResult(
            repositoryRoot,
            Array.AsReadOnly(findings.ToArray()),
            fileCount,
            estimatedBytes,
            frozenWarnings));
        allWarnings.AddRange(repositoryWarnings);
    }

    private void AddWarning(List<OperationWarning> warnings, string path, string message)
    {
        warnings.Add(new OperationWarning(path, message));
        ReportProgress(new OperationProgressEvent(
            ProgressEventKind.Warning,
            ProgressOperation.Audit,
            Path: path,
            Message: message));
    }

    private void ReportRepositoryAuditCompleted(
        string repositoryRoot,
        int current,
        int total,
        long repositoryCount,
        long currentFindingCount,
        long currentEstimatedBytes,
        long findingCount,
        long estimatedBytes,
        long warningCount) =>
        ReportProgress(new OperationProgressEvent(
            ProgressEventKind.RepositoryScanCompleted,
            ProgressOperation.Audit,
            Path: repositoryRoot,
            Current: current,
            Total: total,
            RepositoryCount: repositoryCount,
            CurrentFindingCount: currentFindingCount,
            FindingCount: findingCount,
            CurrentEstimatedBytes: currentEstimatedBytes,
            EstimatedBytes: estimatedBytes,
            WarningCount: warningCount));

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

    private sealed record AuditEntry(ISecureAuditEntry SecureEntry, string RelativePath);

    private sealed class AuditAggregate
    {
        internal static AuditAggregate Empty => new();

        internal long FileCount { get; set; }

        internal long EstimatedBytes { get; set; }

        internal DateTimeOffset? NewestWriteTimeUtc { get; private set; }

        internal bool TimestampUnavailable { get; private set; }

        internal List<AuditFinding> Findings { get; } = [];

        internal void ObserveTimestamp(DateTimeOffset? observed)
        {
            if (observed is null)
            {
                TimestampUnavailable = true;
                return;
            }

            if (NewestWriteTimeUtc is null || observed.Value > NewestWriteTimeUtc.Value)
            {
                NewestWriteTimeUtc = observed.Value;
            }
        }

        internal void Absorb(AuditAggregate child)
        {
            FileCount = FileTreeAnalyzer.SaturatingAdd(FileCount, child.FileCount);
            EstimatedBytes = FileTreeAnalyzer.SaturatingAdd(EstimatedBytes, child.EstimatedBytes);
            TimestampUnavailable |= child.TimestampUnavailable;
            if (child.NewestWriteTimeUtc is not null &&
                (NewestWriteTimeUtc is null || child.NewestWriteTimeUtc.Value > NewestWriteTimeUtc.Value))
            {
                NewestWriteTimeUtc = child.NewestWriteTimeUtc;
            }

            Findings.AddRange(child.Findings);
        }

        internal void ReplaceFindings(AuditFinding finding)
        {
            Findings.Clear();
            Findings.Add(finding);
        }
    }
}
