using RepoGlean.Cli;
using RepoGlean.Git;
using RepoGlean.Progress;
using RepoGlean.Rules;

namespace RepoGlean.Scanning;

public sealed class RepositoryScanner
{
    private readonly GitClient git;
    private readonly FileTreeAnalyzer analyzer;
    private readonly IOperationProgress progress;
    private readonly ProgressOperation operation;

    public RepositoryScanner(GitClient git, FileTreeAnalyzer? analyzer = null)
        : this(git, NullOperationProgress.Instance, ProgressOperation.Scan, analyzer)
    {
    }

    internal RepositoryScanner(
        GitClient git,
        IOperationProgress progress,
        ProgressOperation operation,
        FileTreeAnalyzer? analyzer = null)
    {
        this.git = git ?? throw new ArgumentNullException(nameof(git));
        this.analyzer = analyzer ?? new FileTreeAnalyzer();
        this.progress = progress ?? throw new ArgumentNullException(nameof(progress));
        this.operation = operation;
    }

    public async Task<ScanResult> ScanAsync(
        IReadOnlyList<string> repositoryRoots,
        RuleCatalog ruleCatalog,
        ScanOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repositoryRoots);
        ArgumentNullException.ThrowIfNull(ruleCatalog);
        options ??= ScanOptions.Default;
        var results = new List<RepositoryScanResult>();
        var allWarnings = new List<OperationWarning>();
        var hasCandidateFilters = options.CategoryFilters.Count > 0 || options.Exclusions.Count > 0 || options.MinimumBytes is not null;
        if (repositoryRoots.Count > 0) cancellationToken.ThrowIfCancellationRequested();
        var selectedRepositoryRoots = repositoryRoots
            .Select(Path.GetFullPath)
            .Distinct(RepositoryPathPolicy.PathComparer)
            .Where(repositoryRoot => MatchesRepositoryFilter(repositoryRoot, options.RepositoryFilters))
            .ToArray();
        long cumulativeCandidateCount = 0;
        long cumulativeEstimatedBytes = 0;
        long cumulativeWarningCount = 0;

        for (var index = 0; index < selectedRepositoryRoots.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var repositoryRoot = selectedRepositoryRoots[index];
            var current = index + 1;
            ReportProgress(new OperationProgressEvent(
                ProgressEventKind.RepositoryScanStarted,
                operation,
                Path: repositoryRoot,
                Current: current,
                Total: selectedRepositoryRoots.Length,
                CandidateCount: cumulativeCandidateCount,
                EstimatedBytes: cumulativeEstimatedBytes,
                WarningCount: cumulativeWarningCount));
            IReadOnlyList<string> visiblePaths;
            try
            {
                if (!await git.IsWorkingTreeAsync(repositoryRoot, cancellationToken).ConfigureAwait(false))
                {
                    AddWarning(allWarnings, new OperationWarning(repositoryRoot, "Path is not a Git working tree."));
                    cumulativeWarningCount++;
                    ReportRepositoryScanCompleted(
                        repositoryRoot,
                        current,
                        selectedRepositoryRoots.Length,
                        0,
                        0,
                        cumulativeCandidateCount,
                        cumulativeEstimatedBytes,
                        cumulativeWarningCount);
                    continue;
                }

                visiblePaths = await git.ListVisibleFilesAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
            }
            catch (GitCommandException exception)
            {
                AddWarning(allWarnings, new OperationWarning(repositoryRoot, exception.Message));
                cumulativeWarningCount++;
                ReportRepositoryScanCompleted(
                    repositoryRoot,
                    current,
                    selectedRepositoryRoots.Length,
                    0,
                    0,
                    cumulativeCandidateCount,
                    cumulativeEstimatedBytes,
                    cumulativeWarningCount);
                continue;
            }

            var activeRules = ruleCatalog.Rules
                .Where(rule => options.CategoryFilters.Count == 0 || options.CategoryFilters.Contains(rule.Category))
                .Where(rule => rule.IsActiveFor(visiblePaths))
                .ToArray();
            var repositoryWarnings = new List<OperationWarning>();
            var candidates = await FindCandidatesAsync(
                repositoryRoot,
                visiblePaths,
                activeRules,
                options,
                repositoryWarnings,
                cancellationToken).ConfigureAwait(false);
            candidates.Sort(CompareCandidates);
            allWarnings.AddRange(repositoryWarnings);
            cumulativeWarningCount = FileTreeAnalyzer.SaturatingAdd(cumulativeWarningCount, repositoryWarnings.Count);

            long fileCount = 0;
            long estimatedBytes = 0;
            foreach (var candidate in candidates)
            {
                fileCount = FileTreeAnalyzer.SaturatingAdd(fileCount, candidate.FileCount);
                estimatedBytes = FileTreeAnalyzer.SaturatingAdd(estimatedBytes, candidate.EstimatedBytes);
            }

            cumulativeCandidateCount = FileTreeAnalyzer.SaturatingAdd(cumulativeCandidateCount, candidates.Count);
            cumulativeEstimatedBytes = FileTreeAnalyzer.SaturatingAdd(cumulativeEstimatedBytes, estimatedBytes);
            if (candidates.Count > 0 || !hasCandidateFilters)
            {
                var frozenWarnings = Array.AsReadOnly(repositoryWarnings.ToArray());
                results.Add(new RepositoryScanResult(
                    repositoryRoot,
                    Array.AsReadOnly(candidates.ToArray()),
                    fileCount,
                    estimatedBytes,
                    frozenWarnings));
            }

            ReportRepositoryScanCompleted(
                repositoryRoot,
                current,
                selectedRepositoryRoots.Length,
                candidates.Count,
                estimatedBytes,
                cumulativeCandidateCount,
                cumulativeEstimatedBytes,
                cumulativeWarningCount);
        }

        results.Sort((left, right) =>
        {
            var byBytes = right.EstimatedBytes.CompareTo(left.EstimatedBytes);
            return byBytes != 0 ? byBytes : RepositoryPathPolicy.PathComparer.Compare(left.RepositoryRoot, right.RepositoryRoot);
        });
        long totalFiles = 0;
        long totalBytes = 0;
        foreach (var result in results)
        {
            totalFiles = FileTreeAnalyzer.SaturatingAdd(totalFiles, result.FileCount);
            totalBytes = FileTreeAnalyzer.SaturatingAdd(totalBytes, result.EstimatedBytes);
        }

        return new ScanResult(
            Array.AsReadOnly(results.ToArray()),
            totalFiles,
            totalBytes,
            Array.AsReadOnly(allWarnings.ToArray()));
    }

    private async Task<List<ArtifactCandidate>> FindCandidatesAsync(
        string repositoryRoot,
        IReadOnlyList<string> visiblePaths,
        IReadOnlyList<ArtifactRule> activeRules,
        ScanOptions options,
        List<OperationWarning> warnings,
        CancellationToken cancellationToken)
    {
        var candidates = new List<ArtifactCandidate>();
        var pendingDirectories = new Stack<string>();
        var pendingMatches = new List<PendingMatch>(GitClient.MaximumCheckIgnoreBatchSize);
        pendingDirectories.Push(repositoryRoot);
        while (pendingDirectories.Count > 0 || pendingMatches.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (pendingDirectories.Count == 0)
            {
                await ResolvePendingMatchesAsync(
                    repositoryRoot,
                    pendingMatches,
                    pendingDirectories,
                    visiblePaths,
                    options,
                    candidates,
                    warnings,
                    cancellationToken).ConfigureAwait(false);
                continue;
            }

            var directory = pendingDirectories.Pop();
            IReadOnlyList<string> entries;
            try
            {
                entries = Directory.GetFileSystemEntries(directory);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
                AddWarning(warnings, new OperationWarning(directory, $"Unable to scan directory: {exception.Message}"));
                continue;
            }

            foreach (var entry in entries.OrderBy(path => path, RepositoryPathPolicy.PathComparer))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.Equals(Path.GetFileName(entry), ".git", StringComparison.OrdinalIgnoreCase)) continue;
                var relativePath = RepositoryPathPolicy.NormalizeRelativePath(Path.GetRelativePath(repositoryRoot, entry));
                var reservedQuarantine = RepositoryPathPolicy.IsReservedRootQuarantine(relativePath);
                if (!reservedQuarantine && RepositoryPathPolicy.IsExcluded(entry, relativePath, options.Exclusions)) continue;

                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(entry);
                }
                catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
                {
                    AddWarning(warnings, new OperationWarning(entry, $"Unable to inspect scan entry: {exception.Message}"));
                    continue;
                }

                var isDirectory = (attributes & FileAttributes.Directory) != 0;
                if (reservedQuarantine &&
                    (isDirectory || (attributes & FileAttributes.ReparsePoint) != 0))
                {
                    AddWarning(warnings, new OperationWarning(
                        entry,
                        "Skipped reserved RepoGlean quarantine; inspect or remove the stranded payload manually."));
                    continue;
                }

                if (reservedQuarantine && RepositoryPathPolicy.IsExcluded(entry, relativePath, options.Exclusions)) continue;
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    if (activeRules.Any(rule => rule.Matches(relativePath)))
                    {
                        AddWarning(warnings, new OperationWarning(entry, "Skipped candidate filesystem link, junction, or reparse point."));
                    }

                    continue;
                }
                if (isDirectory && RepositoryPathPolicy.IsRepositoryBoundary(entry))
                {
                    AddWarning(warnings, new OperationWarning(entry, "Skipped nested repository boundary."));
                    continue;
                }

                var matchingRule = activeRules.FirstOrDefault(rule => rule.Matches(relativePath));
                if (matchingRule is not null)
                {
                    pendingMatches.Add(new PendingMatch(entry, relativePath, isDirectory, matchingRule));
                    if (pendingMatches.Count >= GitClient.MaximumCheckIgnoreBatchSize)
                    {
                        await ResolvePendingMatchesAsync(
                            repositoryRoot,
                            pendingMatches,
                            pendingDirectories,
                            visiblePaths,
                            options,
                            candidates,
                            warnings,
                            cancellationToken).ConfigureAwait(false);
                    }

                    continue;
                }

                if (isDirectory) pendingDirectories.Push(entry);
            }
        }

        return candidates;
    }

    private async Task ResolvePendingMatchesAsync(
        string repositoryRoot,
        List<PendingMatch> pendingMatches,
        Stack<string> pendingDirectories,
        IReadOnlyList<string> visiblePaths,
        ScanOptions options,
        List<ArtifactCandidate> candidates,
        List<OperationWarning> warnings,
        CancellationToken cancellationToken)
    {
        var batch = pendingMatches.ToArray();
        pendingMatches.Clear();
        await ResolveIgnoreBatchAsync(
            repositoryRoot,
            batch,
            pendingDirectories,
            visiblePaths,
            options,
            candidates,
            warnings,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task ResolveIgnoreBatchAsync(
        string repositoryRoot,
        IReadOnlyList<PendingMatch> batch,
        Stack<string> pendingDirectories,
        IReadOnlyList<string> visiblePaths,
        ScanOptions options,
        List<ArtifactCandidate> candidates,
        List<OperationWarning> warnings,
        CancellationToken cancellationToken)
    {
        IReadOnlySet<string> ignoredPaths;
        try
        {
            ignoredPaths = await git.GetIgnoredPathsAsync(
                repositoryRoot,
                batch.Select(static match => match.RelativePath).ToArray(),
                cancellationToken).ConfigureAwait(false);
        }
        catch (GitCommandException) when (batch.Count > 1)
        {
            var midpoint = batch.Count / 2;
            await ResolveIgnoreBatchAsync(
                repositoryRoot,
                batch.Take(midpoint).ToArray(),
                pendingDirectories,
                visiblePaths,
                options,
                candidates,
                warnings,
                cancellationToken).ConfigureAwait(false);
            await ResolveIgnoreBatchAsync(
                repositoryRoot,
                batch.Skip(midpoint).ToArray(),
                pendingDirectories,
                visiblePaths,
                options,
                candidates,
                warnings,
                cancellationToken).ConfigureAwait(false);
            return;
        }
        catch (GitCommandException exception)
        {
            AddWarning(warnings, new OperationWarning(batch[0].AbsolutePath, exception.Message));
            return;
        }

        foreach (var match in batch)
        {
            if (!ignoredPaths.Contains(match.RelativePath))
            {
                if (match.IsDirectory) pendingDirectories.Push(match.AbsolutePath);
                continue;
            }

            if (RepositoryPathPolicy.ContainsVisibleContent(match.RelativePath, visiblePaths))
            {
                AddWarning(warnings, new OperationWarning(match.AbsolutePath, "Ignored candidate contains tracked or otherwise visible content."));
                continue;
            }

            var analysis = analyzer.Analyze(match.AbsolutePath, repositoryRoot, cancellationToken);
            foreach (var warning in analysis.Warnings) AddWarning(warnings, warning);
            if (analysis.IsSafe &&
                analysis.Identity is not null &&
                analysis.RepositoryIdentity is not null &&
                (options.MinimumBytes is null || analysis.EstimatedBytes >= options.MinimumBytes.Value))
            {
                candidates.Add(new ArtifactCandidate(
                    repositoryRoot,
                    Path.GetFullPath(match.AbsolutePath),
                    match.RelativePath,
                    match.Rule.Id,
                    match.Rule.Category,
                    match.Rule.Preselected,
                    analysis.FileCount,
                    analysis.EstimatedBytes,
                    analysis.Identity,
                    analysis.RepositoryIdentity,
                    analysis.NewestWriteTimeUtc));
            }
        }
    }

    private void AddWarning(
        List<OperationWarning> warnings,
        OperationWarning warning)
    {
        warnings.Add(warning);
        ReportProgress(new OperationProgressEvent(
            ProgressEventKind.Warning,
            operation,
            Path: warning.Path,
            Message: warning.Message));
    }

    private void ReportRepositoryScanCompleted(
        string repositoryRoot,
        int current,
        int total,
        long currentCandidateCount,
        long currentEstimatedBytes,
        long candidateCount,
        long estimatedBytes,
        long warningCount)
    {
        ReportProgress(new OperationProgressEvent(
            ProgressEventKind.RepositoryScanCompleted,
            operation,
            Path: repositoryRoot,
            Current: current,
            Total: total,
            CurrentCandidateCount: currentCandidateCount,
            CandidateCount: candidateCount,
            CurrentEstimatedBytes: currentEstimatedBytes,
            EstimatedBytes: estimatedBytes,
            WarningCount: warningCount));
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

    private static bool MatchesRepositoryFilter(string repositoryRoot, IReadOnlyList<string> filters)
    {
        if (filters.Count == 0) return true;
        var name = Path.GetFileName(repositoryRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return filters.Any(filter =>
            string.Equals(filter, name, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Path.GetFullPath(filter), repositoryRoot, RepositoryPathPolicy.PathComparison));
    }

    private static int CompareCandidates(ArtifactCandidate left, ArtifactCandidate right)
    {
        var byBytes = right.EstimatedBytes.CompareTo(left.EstimatedBytes);
        return byBytes != 0 ? byBytes : string.Compare(left.RelativePath, right.RelativePath, StringComparison.Ordinal);
    }

    private sealed record PendingMatch(string AbsolutePath, string RelativePath, bool IsDirectory, ArtifactRule Rule);
}
