using RepoGlean.Git;
using RepoGlean.Progress;
using RepoGlean.Rules;
using RepoGlean.Scanning;

namespace RepoGlean.Auditing;

public sealed class RepositoryAuditor
{
    private readonly GitClient git;
    private readonly IVolumeBoundary volumeBoundary;
    private readonly IFileTimestampProvider timestampProvider;
    private readonly IOperationProgress progress;

    public RepositoryAuditor(GitClient git)
        : this(git, new FileSystemIdentityProvider(), new FileTimestampProvider(), NullOperationProgress.Instance)
    {
    }

    internal RepositoryAuditor(
        GitClient git,
        IVolumeBoundary volumeBoundary,
        IFileTimestampProvider timestampProvider,
        IOperationProgress progress)
    {
        this.git = git ?? throw new ArgumentNullException(nameof(git));
        this.volumeBoundary = volumeBoundary ?? throw new ArgumentNullException(nameof(volumeBoundary));
        this.timestampProvider = timestampProvider ?? throw new ArgumentNullException(nameof(timestampProvider));
        this.progress = progress ?? throw new ArgumentNullException(nameof(progress));
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
            .Select(Path.GetFullPath)
            .Distinct(RepositoryPathPolicy.PathComparer)
            .Where(repositoryRoot => MatchesRepositoryFilter(repositoryRoot, options.RepositoryFilters))
            .ToArray();
        var repositories = new List<RepositoryAuditResult>();
        var allWarnings = new List<OperationWarning>();

        foreach (var repositoryRoot in selectedRepositoryRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<string> visiblePaths;
            try
            {
                if (!await git.IsWorkingTreeAsync(repositoryRoot, cancellationToken).ConfigureAwait(false))
                {
                    allWarnings.Add(new OperationWarning(repositoryRoot, "Path is not a Git working tree."));
                    continue;
                }

                visiblePaths = await git.ListVisibleFilesAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
            }
            catch (GitCommandException exception)
            {
                allWarnings.Add(new OperationWarning(repositoryRoot, exception.Message));
                continue;
            }

            var repositoryWarnings = new List<OperationWarning>();
            var activeRules = ruleCatalog.Rules
                .Where(rule => rule.IsActiveFor(visiblePaths))
                .ToArray();
            if (!volumeBoundary.TryGetMountIdentity(
                    repositoryRoot,
                    out var repositoryMount,
                    out var repositoryMountError) ||
                repositoryMount is null)
            {
                AddWarning(
                    repositoryWarnings,
                    repositoryRoot,
                    repositoryMountError ?? "Unable to identify the repository filesystem mount.");
                AddRepositoryResult(repositoryRoot, [], repositoryWarnings, repositories, allWarnings);
                continue;
            }

            var aggregate = await AuditDirectoryContentsAsync(
                repositoryRoot,
                repositoryRoot,
                visiblePaths,
                activeRules,
                options,
                repositoryMount,
                repositoryWarnings,
                cancellationToken).ConfigureAwait(false);
            var findings = aggregate.Findings
                .Where(finding => finding.EstimatedBytes >= options.MinimumBytes)
                .OrderByDescending(finding => finding.EstimatedBytes)
                .ThenBy(finding => finding.RelativePath, StringComparer.Ordinal)
                .ToArray();
            AddRepositoryResult(repositoryRoot, findings, repositoryWarnings, repositories, allWarnings);
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
        string directory,
        IReadOnlyList<string> visiblePaths,
        IReadOnlyList<ArtifactRule> activeRules,
        AuditOptions options,
        FileSystemMountIdentity repositoryMount,
        List<OperationWarning> warnings,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> paths;
        try
        {
            paths = Directory.GetFileSystemEntries(directory)
                .OrderBy(path => path, RepositoryPathPolicy.PathComparer)
                .ToArray();
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            AddWarning(warnings, directory, $"Unable to enumerate audit directory: {exception.Message}");
            return AuditAggregate.Empty;
        }

        var entries = new List<AuditEntry>(paths.Count);
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(Path.GetFileName(path), ".git", StringComparison.OrdinalIgnoreCase)) continue;
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

            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(path);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
                AddWarning(warnings, path, $"Unable to inspect audit entry: {exception.Message}");
                continue;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                AddWarning(warnings, path, "Skipped audit filesystem link, junction, or reparse point.");
                continue;
            }

            var isDirectory = (attributes & FileAttributes.Directory) != 0;
            if (isDirectory && RepositoryPathPolicy.IsRepositoryBoundary(path))
            {
                AddWarning(warnings, path, "Skipped nested repository boundary.");
                continue;
            }

            if (!volumeBoundary.TryGetMountIdentity(path, out var mount, out var mountError) || mount is null)
            {
                AddWarning(warnings, path, mountError ?? "Unable to identify the audit path filesystem mount.");
                continue;
            }

            if (mount != repositoryMount)
            {
                AddWarning(warnings, path, "Skipped path on a different filesystem mount or volume.");
                continue;
            }

            if (activeRules.Any(rule => rule.Matches(relativePath))) continue;
            if (!isDirectory && RepositoryPathPolicy.ContainsVisibleContent(relativePath, visiblePaths)) continue;
            entries.Add(new AuditEntry(path, relativePath, isDirectory));
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
            var child = entry.IsDirectory
                ? await AuditDirectoryAsync(
                    repositoryRoot,
                    entry,
                    match,
                    visiblePaths,
                    activeRules,
                    options,
                    repositoryMount,
                    warnings,
                    cancellationToken).ConfigureAwait(false)
                : AuditFile(repositoryRoot, entry, match, warnings);
            aggregate.Absorb(child);
        }

        return aggregate;
    }

    private async Task<AuditAggregate> AuditDirectoryAsync(
        string repositoryRoot,
        AuditEntry entry,
        GitIgnoreMatch match,
        IReadOnlyList<string> visiblePaths,
        IReadOnlyList<ArtifactRule> activeRules,
        AuditOptions options,
        FileSystemMountIdentity repositoryMount,
        List<OperationWarning> warnings,
        CancellationToken cancellationToken)
    {
        var aggregate = await AuditDirectoryContentsAsync(
            repositoryRoot,
            entry.AbsolutePath,
            visiblePaths,
            activeRules,
            options,
            repositoryMount,
            warnings,
            cancellationToken).ConfigureAwait(false);
        if (aggregate.FileCount == 0) return AuditAggregate.Empty;

        aggregate.ObserveTimestamp(timestampProvider, entry.AbsolutePath);
        if (!match.IsIgnored) return aggregate;

        aggregate.ReplaceFindings(new AuditFinding(
            repositoryRoot,
            Path.GetFullPath(entry.AbsolutePath),
            entry.RelativePath,
            aggregate.FileCount,
            aggregate.EstimatedBytes,
            aggregate.TimestampUnavailable ? null : aggregate.NewestWriteTimeUtc,
            match));
        return aggregate;
    }

    private AuditAggregate AuditFile(
        string repositoryRoot,
        AuditEntry entry,
        GitIgnoreMatch match,
        List<OperationWarning> warnings)
    {
        if (!match.IsIgnored) return AuditAggregate.Empty;

        long length;
        try
        {
            length = new FileInfo(entry.AbsolutePath).Length;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            AddWarning(warnings, entry.AbsolutePath, $"Unable to read audit file length: {exception.Message}");
            return AuditAggregate.Empty;
        }

        var aggregate = new AuditAggregate
        {
            FileCount = 1,
            EstimatedBytes = length,
        };
        aggregate.ObserveTimestamp(timestampProvider, entry.AbsolutePath);
        aggregate.ReplaceFindings(new AuditFinding(
            repositoryRoot,
            Path.GetFullPath(entry.AbsolutePath),
            entry.RelativePath,
            1,
            length,
            aggregate.TimestampUnavailable ? null : aggregate.NewestWriteTimeUtc,
            match));
        return aggregate;
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
            AddWarning(warnings, batch[0].AbsolutePath, exception.Message);
        }
    }

    private static bool MatchesRepositoryFilter(string repositoryRoot, IReadOnlyList<string> filters)
    {
        if (filters.Count == 0) return true;
        var name = Path.GetFileName(repositoryRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return filters.Any(filter =>
            string.Equals(filter, name, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                Path.GetFullPath(filter),
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

    private static void AddWarning(List<OperationWarning> warnings, string path, string message) =>
        warnings.Add(new OperationWarning(path, message));

    private sealed record AuditEntry(string AbsolutePath, string RelativePath, bool IsDirectory);

    private sealed class AuditAggregate
    {
        internal static AuditAggregate Empty => new();

        internal long FileCount { get; set; }

        internal long EstimatedBytes { get; set; }

        internal DateTimeOffset? NewestWriteTimeUtc { get; private set; }

        internal bool TimestampUnavailable { get; private set; }

        internal List<AuditFinding> Findings { get; } = [];

        internal void ObserveTimestamp(IFileTimestampProvider provider, string path)
        {
            if (!provider.TryGetLastWriteTimeUtc(path, out var observed))
            {
                TimestampUnavailable = true;
                return;
            }

            if (NewestWriteTimeUtc is null || observed > NewestWriteTimeUtc.Value)
            {
                NewestWriteTimeUtc = observed;
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
