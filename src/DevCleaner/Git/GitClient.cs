namespace DevCleaner.Git;

public sealed class GitClient
{
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
        if (result.ExitCode != 0) return false;
        return string.Equals(result.StandardOutput.Trim(), "true", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<IReadOnlyList<string>> ListVisibleFilesAsync(string repositoryRoot, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        var result = await runner.RunAsync(
            ["-C", Path.GetFullPath(repositoryRoot), "ls-files", "-co", "--exclude-standard", "-z"],
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

    private static string NormalizeRelativePath(string path) => path.Replace('\\', '/').TrimStart('/');

    private static void EnsureSuccess(ProcessResult result, string operation)
    {
        if (result.ExitCode != 0) throw CreateFailure(result, operation);
    }

    private static InvalidOperationException CreateFailure(ProcessResult result, string operation)
    {
        var detail = string.IsNullOrWhiteSpace(result.StandardError) ? "No diagnostic output was provided." : result.StandardError.Trim();
        return new InvalidOperationException($"{operation} failed with exit code {result.ExitCode}: {detail}");
    }
}
