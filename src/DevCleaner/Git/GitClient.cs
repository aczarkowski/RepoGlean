namespace DevCleaner.Git;

public sealed class GitUnavailableException : InvalidOperationException
{
    public GitUnavailableException(string message)
        : base(message)
    {
    }

    public GitUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class GitCommandException : InvalidOperationException
{
    public GitCommandException(string message)
        : base(message)
    {
    }
}

public sealed class GitClient
{
    internal const int MaximumCheckIgnoreBatchSize = 128;
    internal const string QuarantineDirectoryPrefix = ".devcleaner-quarantine-";

    private readonly ProcessRunner runner;

    public GitClient(string executable = "git", IReadOnlyDictionary<string, string?>? environment = null)
    {
        runner = new ProcessRunner(executable, environment);
    }

    public async Task<string> GetVersionAsync(CancellationToken cancellationToken = default)
    {
        var result = await runner.RunAsync(["--version"], null, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(result, "git --version");
        return result.StandardOutput.Trim();
    }

    public async Task<bool> IsWorkingTreeAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var result = await runner.RunAsync(
            ["-C", Path.GetFullPath(path), "rev-parse", "--is-inside-work-tree"],
            null,
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0) throw CreateFailure(result, "git rev-parse");
        return string.Equals(result.StandardOutput.Trim(), "true", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<string> GetRepositoryRootAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var result = await runner.RunAsync(
            ["-C", Path.GetFullPath(path), "rev-parse", "--show-toplevel"],
            null,
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(result, "git rev-parse --show-toplevel");
        return Path.GetFullPath(result.StandardOutput.Trim());
    }

    public Task<IReadOnlyList<string>> ListVisibleFilesAsync(
        string repositoryRoot,
        CancellationToken cancellationToken = default) =>
        ListVisibleFilesCoreAsync(repositoryRoot, excludedRepositoryRelativePath: null, cancellationToken);

    internal Task<IReadOnlyList<string>> ListVisibleFilesExcludingAsync(
        string repositoryRoot,
        string excludedRepositoryRelativePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(excludedRepositoryRelativePath);
        ValidateRelativePath(excludedRepositoryRelativePath);
        return ListVisibleFilesCoreAsync(repositoryRoot, NormalizeRelativePath(excludedRepositoryRelativePath), cancellationToken);
    }

    private async Task<IReadOnlyList<string>> ListVisibleFilesCoreAsync(
        string repositoryRoot,
        string? excludedRepositoryRelativePath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        var arguments = new List<string>
        {
            "-C", Path.GetFullPath(repositoryRoot), "ls-files", "-co", "--exclude-standard", "-z", "--", ".",
            $":(top,exclude,glob,icase){QuarantineDirectoryPrefix}*/**",
        };
        if (excludedRepositoryRelativePath is not null)
        {
            arguments.Add($":(top,exclude,literal){excludedRepositoryRelativePath}");
        }

        var result = await runner.RunAsync(
            arguments,
            null,
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(result, "git ls-files");
        return Array.AsReadOnly(result.StandardOutput
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(NormalizeRelativePath)
            .Distinct(StringComparer.Ordinal)
            .ToArray());
    }

    public async Task<bool> IsIgnoredAsync(
        string repositoryRoot,
        string repositoryRelativePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRelativePath);
        if (Path.IsPathRooted(repositoryRelativePath) || repositoryRelativePath.Replace('\\', '/').Split('/').Any(segment => segment is "." or ".."))
        {
            throw new ArgumentException("Git paths must be repository-relative and cannot contain dot segments.", nameof(repositoryRelativePath));
        }

        var result = await runner.RunAsync(
            ["-C", Path.GetFullPath(repositoryRoot), "check-ignore", "-q", "--", NormalizeRelativePath(repositoryRelativePath)],
            null,
            cancellationToken).ConfigureAwait(false);
        return result.ExitCode switch
        {
            0 => true,
            1 => false,
            _ => throw CreateFailure(result, "git check-ignore"),
        };
    }

    public async Task<bool> IsIgnoredWithoutIndexAsync(
        string repositoryRoot,
        string repositoryRelativePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRelativePath);
        ValidateRelativePath(repositoryRelativePath.TrimEnd('/'));
        var result = await runner.RunAsync(
            ["-C", Path.GetFullPath(repositoryRoot), "check-ignore", "--no-index", "-q", "--", NormalizeRelativePath(repositoryRelativePath)],
            null,
            cancellationToken).ConfigureAwait(false);
        return result.ExitCode switch
        {
            0 => true,
            1 => false,
            _ => throw CreateFailure(result, "git check-ignore --no-index"),
        };
    }

    public async Task<IReadOnlySet<string>> GetIgnoredPathsAsync(
        string repositoryRoot,
        IReadOnlyList<string> repositoryRelativePaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentNullException.ThrowIfNull(repositoryRelativePaths);
        var normalizedPaths = new string[repositoryRelativePaths.Count];
        for (var index = 0; index < repositoryRelativePaths.Count; index++)
        {
            var path = repositoryRelativePaths[index];
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            ValidateRelativePath(path);
            normalizedPaths[index] = NormalizeRelativePath(path);
        }

        var ignoredPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var batch in normalizedPaths.Chunk(MaximumCheckIgnoreBatchSize))
        {
            var standardInput = string.Concat(batch.Select(static path => $"{path}\0"));
            var result = await runner.RunAsync(
                ["-C", Path.GetFullPath(repositoryRoot), "check-ignore", "--stdin", "-z"],
                null,
                cancellationToken,
                standardInput).ConfigureAwait(false);
            if (result.ExitCode is not (0 or 1)) throw CreateFailure(result, "git check-ignore");
            foreach (var path in result.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                ignoredPaths.Add(NormalizeRelativePath(path));
            }
        }

        return ignoredPaths;
    }

    public async Task<bool> ContainsTrackedContentAsync(
        string repositoryRoot,
        string repositoryRelativePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRelativePath);
        ValidateRelativePath(repositoryRelativePath);
        var result = await runner.RunAsync(
            ["-C", Path.GetFullPath(repositoryRoot), "ls-files", "-z", "--", NormalizeRelativePath(repositoryRelativePath)],
            null,
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(result, "git ls-files");
        return result.StandardOutput.Length > 0;
    }

    private static void ValidateRelativePath(string repositoryRelativePath)
    {
        if (Path.IsPathRooted(repositoryRelativePath) || repositoryRelativePath.Replace('\\', '/').Split('/').Any(segment => segment is "." or ".."))
        {
            throw new ArgumentException("Git paths must be repository-relative and cannot contain dot segments.", nameof(repositoryRelativePath));
        }
    }

    private static string NormalizeRelativePath(string path) => path.Replace('\\', '/').TrimStart('/');

    private static void EnsureSuccess(ProcessResult result, string operation)
    {
        if (result.ExitCode != 0) throw CreateFailure(result, operation);
    }

    private static GitCommandException CreateFailure(ProcessResult result, string operation)
    {
        var detail = string.IsNullOrWhiteSpace(result.StandardError) ? "No diagnostic output was provided." : result.StandardError.Trim();
        return new GitCommandException($"{operation} failed with exit code {result.ExitCode}: {detail}");
    }
}
