using DevCleaner.Cli;
using DevCleaner.Git;
using DevCleaner.Rules;

namespace DevCleaner.Scanning;

public sealed class RepositoryScanner
{
    private readonly GitClient git;
    private readonly FileTreeAnalyzer analyzer;

    public RepositoryScanner(GitClient git, FileTreeAnalyzer? analyzer = null)
    {
        this.git = git ?? throw new ArgumentNullException(nameof(git));
        this.analyzer = analyzer ?? new FileTreeAnalyzer();
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

        foreach (var repositoryRootValue in repositoryRoots.Distinct(PathComparer))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var repositoryRoot = Path.GetFullPath(repositoryRootValue);
            if (!MatchesRepositoryFilter(repositoryRoot, options.RepositoryFilters)) continue;
            if (!await git.IsWorkingTreeAsync(repositoryRoot, cancellationToken).ConfigureAwait(false))
            {
                allWarnings.Add(new OperationWarning(repositoryRoot, "Path is not a Git working tree."));
                continue;
            }

            var visiblePaths = await git.ListVisibleFilesAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
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
            if (candidates.Count == 0 && hasCandidateFilters) continue;

            long fileCount = 0;
            long estimatedBytes = 0;
            foreach (var candidate in candidates)
            {
                fileCount = FileTreeAnalyzer.SaturatingAdd(fileCount, candidate.FileCount);
                estimatedBytes = FileTreeAnalyzer.SaturatingAdd(estimatedBytes, candidate.EstimatedBytes);
            }

            var frozenWarnings = Array.AsReadOnly(repositoryWarnings.ToArray());
            results.Add(new RepositoryScanResult(
                repositoryRoot,
                Array.AsReadOnly(candidates.ToArray()),
                fileCount,
                estimatedBytes,
                frozenWarnings));
        }

        results.Sort((left, right) =>
        {
            var byBytes = right.EstimatedBytes.CompareTo(left.EstimatedBytes);
            return byBytes != 0 ? byBytes : PathComparer.Compare(left.RepositoryRoot, right.RepositoryRoot);
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
        var pending = new Stack<string>();
        pending.Push(repositoryRoot);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            IReadOnlyList<string> entries;
            try
            {
                entries = Directory.GetFileSystemEntries(directory);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
                warnings.Add(new OperationWarning(directory, $"Unable to scan directory: {exception.Message}"));
                continue;
            }

            foreach (var entry in entries.OrderBy(path => path, PathComparer))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.Equals(Path.GetFileName(entry), ".git", StringComparison.OrdinalIgnoreCase)) continue;
                var relativePath = NormalizeRelativePath(Path.GetRelativePath(repositoryRoot, entry));
                if (IsExcluded(entry, relativePath, options.Exclusions)) continue;

                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(entry);
                }
                catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
                {
                    warnings.Add(new OperationWarning(entry, $"Unable to inspect scan entry: {exception.Message}"));
                    continue;
                }

                var isDirectory = (attributes & FileAttributes.Directory) != 0;
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                if (isDirectory && IsRepositoryBoundary(entry))
                {
                    warnings.Add(new OperationWarning(entry, "Skipped nested repository boundary."));
                    continue;
                }

                var matchingRule = activeRules.FirstOrDefault(rule => rule.Matches(relativePath));
                if (matchingRule is not null && await git.IsIgnoredAsync(repositoryRoot, relativePath, cancellationToken).ConfigureAwait(false))
                {
                    if (ContainsVisibleContent(relativePath, visiblePaths))
                    {
                        warnings.Add(new OperationWarning(entry, "Ignored candidate contains tracked or otherwise visible content."));
                        continue;
                    }

                    var analysis = analyzer.Analyze(entry, repositoryRoot, cancellationToken);
                    warnings.AddRange(analysis.Warnings);
                    if (analysis.IsSafe && analysis.Identity is not null && (options.MinimumBytes is null || analysis.EstimatedBytes >= options.MinimumBytes.Value))
                    {
                        candidates.Add(new ArtifactCandidate(
                            repositoryRoot,
                            Path.GetFullPath(entry),
                            relativePath,
                            matchingRule.Id,
                            matchingRule.Category,
                            matchingRule.Preselected,
                            analysis.FileCount,
                            analysis.EstimatedBytes,
                            analysis.Identity));
                    }

                    continue;
                }

                if (isDirectory) pending.Push(entry);
            }
        }

        return candidates;
    }

    private static bool ContainsVisibleContent(string candidateRelativePath, IReadOnlyList<string> visiblePaths)
    {
        var prefix = candidateRelativePath.EndsWith("/", StringComparison.Ordinal) ? candidateRelativePath : $"{candidateRelativePath}/";
        return visiblePaths.Any(path => string.Equals(path, candidateRelativePath, StringComparison.Ordinal) || path.StartsWith(prefix, StringComparison.Ordinal));
    }

    private static bool IsRepositoryBoundary(string directory) =>
        Directory.Exists(Path.Combine(directory, ".git")) || File.Exists(Path.Combine(directory, ".git"));

    private static bool MatchesRepositoryFilter(string repositoryRoot, IReadOnlyList<string> filters)
    {
        if (filters.Count == 0) return true;
        var name = Path.GetFileName(repositoryRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return filters.Any(filter =>
            string.Equals(filter, name, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Path.GetFullPath(filter), repositoryRoot, PathComparison));
    }

    private static bool IsExcluded(string absolutePath, string relativePath, IReadOnlyList<string> exclusions)
    {
        foreach (var exclusion in exclusions)
        {
            if (string.IsNullOrWhiteSpace(exclusion)) continue;
            if (Path.IsPathRooted(exclusion) && RepositoryDiscovery.IsSameOrDescendant(absolutePath, Path.GetFullPath(exclusion))) return true;
            var normalized = NormalizeRelativePath(exclusion).TrimEnd('/');
            if (string.Equals(relativePath, normalized, StringComparison.OrdinalIgnoreCase) ||
                relativePath.StartsWith($"{normalized}/", StringComparison.OrdinalIgnoreCase) ||
                GlobMatcher.IsMatch(normalized, relativePath))
            {
                return true;
            }
        }

        return false;
    }

    private static int CompareCandidates(ArtifactCandidate left, ArtifactCandidate right)
    {
        var byBytes = right.EstimatedBytes.CompareTo(left.EstimatedBytes);
        return byBytes != 0 ? byBytes : string.Compare(left.RelativePath, right.RelativePath, StringComparison.Ordinal);
    }

    private static string NormalizeRelativePath(string path) => path.Replace('\\', '/').TrimStart('/');

    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
