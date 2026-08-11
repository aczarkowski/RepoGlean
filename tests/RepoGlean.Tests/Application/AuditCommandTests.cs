using System.Security.Cryptography;
using System.Text.Json;
using RepoGlean.Tests.Support;

namespace RepoGlean.Tests.Application;

public sealed class AuditCommandTests
{
    [Fact]
    public async Task Audit_accepts_cross_mounts_option()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await CreateAuditRepositoryAsync(temporary.GetPath("repository"), "unknown", 23);

        var result = await RunAsync([
            "audit", repository.Path, "--cross-mounts", "--min-size", "0", "--format", "json",
        ]);

        Assert.NotEqual(2, result.ExitCode);
    }

    [Fact]
    public async Task Audit_refuses_a_repository_root_link_with_a_trailing_separator_after_discovery()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var temporary = new TemporaryDirectory();
        var repository = await CreateAuditRepositoryAsync(temporary.GetPath("repository"), "unknown", 23);
        var link = temporary.GetPath("repository-link");
        Directory.CreateSymbolicLink(link, repository.Path);

        var result = await RunAsync([
            "audit", link + Path.DirectorySeparatorChar, "--min-size", "0", "--format", "json",
        ]);

        Assert.Equal(3, result.ExitCode);
        using var document = JsonDocument.Parse(result.Stdout);
        Assert.Equal("partial", document.RootElement.GetProperty("status").GetString());
        Assert.Equal(0, document.RootElement.GetProperty("totals").GetProperty("findingCount").GetInt64());
        Assert.Contains(document.RootElement.GetProperty("warnings").EnumerateArray(), warning =>
            warning.GetProperty("path").GetString()!.TrimEnd(Path.DirectorySeparatorChar) == link &&
            warning.GetProperty("message").GetString()!.Contains("link", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Audit_uses_cli_roots_over_configured_roots_and_configured_roots_over_home()
    {
        using var temporary = new TemporaryDirectory();
        var home = await CreateAuditRepositoryAsync(temporary.GetPath("home"), "home-data", 2);
        var configured = await CreateAuditRepositoryAsync(temporary.GetPath("configured"), "configured-data", 3);
        var requested = await CreateAuditRepositoryAsync(temporary.GetPath("requested"), "requested-data", 5);
        var configPath = temporary.GetPath("config.json");
        File.WriteAllText(configPath, $$"""{"schemaVersion":1,"roots":["{{JsonEncodedText.Encode(configured.Path)}}"]}""");

        var commandLine = await RunAsync(
            ["audit", requested.Path, "--config", configPath, "--min-size", "0", "--format", "json"],
            homeDirectory: home.Path);
        var fromConfig = await RunAsync(
            ["audit", "--config", configPath, "--min-size", "0", "--format", "json"],
            homeDirectory: home.Path);
        File.WriteAllText(configPath, "{\"schemaVersion\":1,\"roots\":[]}");
        var fromHome = await RunAsync(
            ["audit", "--config", configPath, "--min-size", "0", "--format", "json"],
            homeDirectory: home.Path);

        AssertAuditRootAndBytes(commandLine, requested.Path, 5);
        AssertAuditRootAndBytes(fromConfig, configured.Path, 3);
        AssertAuditRootAndBytes(fromHome, home.Path, 2);
    }

    [Fact]
    public async Task Audit_combines_configuration_and_command_line_exclusions()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await GitTestRepository.CreateAsync(temporary.GetPath("repo"));
        repository.Write(".gitignore", "config-hidden/\ncli-hidden/\nincluded/\n");
        repository.WriteBytes("config-hidden/payload.bin", 2);
        repository.WriteBytes("cli-hidden/payload.bin", 3);
        repository.WriteBytes("included/payload.bin", 5);
        await repository.CommitAllAsync();
        var configPath = temporary.GetPath("config.json");
        File.WriteAllText(configPath, $$"""
            {"schemaVersion":1,"roots":["{{JsonEncodedText.Encode(repository.Path)}}"],"excludes":["config-hidden"]}
            """);

        var result = await RunAsync([
            "audit", "--config", configPath, "--exclude", "cli-hidden",
            "--min-size", "0", "--format", "json",
        ]);

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.Stdout);
        var findings = document.RootElement.GetProperty("repositories")[0].GetProperty("findings");
        Assert.Single(findings.EnumerateArray());
        Assert.Equal("included", findings[0].GetProperty("relativePath").GetString());
        Assert.Equal(5, document.RootElement.GetProperty("totals").GetProperty("estimatedBytes").GetInt64());
    }

    [Fact]
    public async Task Audit_uses_exact_default_minimum_and_accepts_positive_and_zero_overrides()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await GitTestRepository.CreateAsync(temporary.GetPath("repo"));
        repository.Write(".gitignore", "below/\nexact/\nsmall/\n");
        SetSparseLength(repository.GetPath("below/payload.bin"), 104_857_599);
        SetSparseLength(repository.GetPath("exact/payload.bin"), 104_857_600);
        repository.WriteBytes("small/payload.bin", 5);
        await repository.CommitAllAsync();

        var omitted = await RunAsync(["audit", repository.Path, "--format", "json"]);
        var positive = await RunAsync(["audit", repository.Path, "--min-size", "104857601B", "--format", "json"]);
        var zero = await RunAsync(["audit", repository.Path, "--min-size", "0", "--format", "json"]);

        Assert.Equal(["exact"], FindingPaths(omitted));
        Assert.Empty(FindingPaths(positive));
        Assert.Equal(["exact", "below", "small"], FindingPaths(zero));
    }

    [Fact]
    public async Task Audit_json_is_one_machine_document_and_verbose_progress_is_append_only_stderr()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await CreateAuditRepositoryAsync(temporary.GetPath("repo"), "unknown", 18);

        var ordinary = await RunAsync(["audit", repository.Path, "--min-size", "0", "--format", "json"]);
        var verbose = await RunAsync(["audit", repository.Path, "--min-size", "0", "--format", "json", "--verbose"]);

        Assert.Equal(0, ordinary.ExitCode);
        Assert.Equal(string.Empty, ordinary.Stderr);
        using (var document = JsonDocument.Parse(ordinary.Stdout))
        {
            Assert.Equal("audit", document.RootElement.GetProperty("operation").GetString());
            Assert.Equal("success", document.RootElement.GetProperty("status").GetString());
        }

        Assert.Equal(0, verbose.ExitCode);
        using (var document = JsonDocument.Parse(verbose.Stdout))
        {
            Assert.Equal(1, document.RootElement.GetProperty("totals").GetProperty("findingCount").GetInt64());
        }

        AssertContainsInOrder(
            verbose.Stderr,
            "Discovering repositories under",
            $"Auditing [1/1] {repository.Path}...",
            $"Found 1 finding in {Path.GetFileName(repository.Path)} (18 B estimated).",
            "Audit complete: 1 repository, 1 finding, 0 warnings.");
        Assert.DoesNotContain('\r', verbose.Stderr);
        Assert.DoesNotContain('\u001b', verbose.Stderr);
    }

    [Fact]
    public async Task Audit_quiet_retains_the_human_summary_and_no_findings_is_success()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await GitTestRepository.CreateAsync(temporary.GetPath("repo"));
        repository.Write("tracked.txt", "tracked");
        await repository.CommitAllAsync();

        var result = await RunAsync(["audit", repository.Path, "--quiet"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.Stderr);
        Assert.Contains("Audit summary", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("Repositories: 1", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("Unclassified findings: 0", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("Minimum finding size: 100 MiB estimated", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("Roots:", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Audit_maps_warnings_missing_git_bad_options_and_pre_cancellation_to_exact_statuses()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await GitTestRepository.CreateAsync(temporary.GetPath("repo"));
        repository.Write("tracked.txt", "tracked");
        await repository.CommitAllAsync();
        var missingRoot = temporary.GetPath("missing");

        var warning = await RunAsync(["audit", repository.Path, missingRoot, "--format", "json"]);
        var missingGit = await RunAsync(
            ["audit", repository.Path, "--format", "json"],
            gitExecutable: "repoglean-missing-git");
        var badOptions = await RunAsync(["audit", "--details"]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var interrupted = await RunAsync(
            ["audit", repository.Path, "--format", "json"],
            cancellationToken: cancellation.Token);

        Assert.Equal(3, warning.ExitCode);
        Assert.Equal("partial", ParseRoot(warning).GetProperty("status").GetString());
        Assert.Equal(1, missingGit.ExitCode);
        Assert.Equal("audit", ParseRoot(missingGit).GetProperty("operation").GetString());
        Assert.Equal("failed", ParseRoot(missingGit).GetProperty("status").GetString());
        Assert.Equal(2, badOptions.ExitCode);
        Assert.Equal(string.Empty, badOptions.Stdout);
        Assert.Contains("not valid with audit", badOptions.Stderr, StringComparison.Ordinal);
        Assert.Equal(130, interrupted.ExitCode);
        Assert.Equal("audit", ParseRoot(interrupted).GetProperty("operation").GetString());
        Assert.Equal("interrupted", ParseRoot(interrupted).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Audit_does_not_change_tracked_untracked_ignored_or_git_metadata()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await GitTestRepository.CreateAsync(temporary.GetPath("repo"));
        repository.Write(".gitignore", "ignored/\n");
        repository.Write("tracked.txt", "tracked");
        await repository.CommitAllAsync();
        repository.Write("untracked.txt", "untracked");
        repository.Write("ignored/payload.txt", "ignored");
        var beforeStatus = await repository.GitAsync("status", "--porcelain=v1", "--untracked-files=all", "--ignored=matching");
        var before = SnapshotTree(repository.Path);

        var result = await RunAsync(["audit", repository.Path, "--min-size", "0", "--format", "json"]);

        var afterStatus = await repository.GitAsync("status", "--porcelain=v1", "--untracked-files=all", "--ignored=matching");
        var after = SnapshotTree(repository.Path);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(beforeStatus, afterStatus);
        Assert.Equal(before, after);
        Assert.Equal("tracked", File.ReadAllText(repository.GetPath("tracked.txt")));
        Assert.Equal("untracked", File.ReadAllText(repository.GetPath("untracked.txt")));
        Assert.Equal("ignored", File.ReadAllText(repository.GetPath("ignored/payload.txt")));
    }

    [Fact]
    public async Task Audit_silently_carves_a_tracked_symbolic_link_and_remains_successful()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var temporary = new TemporaryDirectory();
        var repository = await GitTestRepository.CreateAsync(temporary.GetPath("repo"));
        var external = temporary.GetPath("external");
        Directory.CreateDirectory(external);
        File.WriteAllBytes(Path.Combine(external, "outside.bin"), new byte[31]);
        Directory.CreateSymbolicLink(repository.GetPath("visible-link"), external);
        await repository.CommitAllAsync();

        var result = await RunAsync(["audit", repository.Path, "--min-size", "0", "--format", "json"]);

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.Stdout);
        Assert.Equal("success", document.RootElement.GetProperty("status").GetString());
        Assert.Empty(document.RootElement.GetProperty("warnings").EnumerateArray());
        Assert.Equal(0, document.RootElement.GetProperty("totals").GetProperty("estimatedBytes").GetInt64());
    }

    [Fact]
    public async Task Audit_silently_carves_a_classified_ignored_symbolic_link_and_remains_successful()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var temporary = new TemporaryDirectory();
        var repository = await GitTestRepository.CreateAsync(temporary.GetPath("repo"));
        repository.Write("project.csproj", "<Project />");
        repository.Write(".gitignore", "obj\n");
        await repository.CommitAllAsync();
        var external = temporary.GetPath("external");
        Directory.CreateDirectory(external);
        File.WriteAllBytes(Path.Combine(external, "outside.bin"), new byte[31]);
        Directory.CreateSymbolicLink(repository.GetPath("obj"), external);

        var result = await RunAsync(["audit", repository.Path, "--min-size", "0", "--format", "json"]);

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.Stdout);
        Assert.Equal("success", document.RootElement.GetProperty("status").GetString());
        Assert.Empty(document.RootElement.GetProperty("warnings").EnumerateArray());
        Assert.Equal(0, document.RootElement.GetProperty("totals").GetProperty("estimatedBytes").GetInt64());
    }

    [Fact]
    public async Task Audit_warns_for_an_unclassified_ignored_symbolic_link_and_remains_partial()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var temporary = new TemporaryDirectory();
        var repository = await GitTestRepository.CreateAsync(temporary.GetPath("repo"));
        repository.Write(".gitignore", "unknown\n");
        await repository.CommitAllAsync();
        var external = temporary.GetPath("external");
        Directory.CreateDirectory(external);
        File.WriteAllBytes(Path.Combine(external, "outside.bin"), new byte[31]);
        var link = repository.GetPath("unknown");
        Directory.CreateSymbolicLink(link, external);

        var result = await RunAsync(["audit", repository.Path, "--min-size", "0", "--format", "json"]);

        Assert.Equal(3, result.ExitCode);
        using var document = JsonDocument.Parse(result.Stdout);
        Assert.Equal("partial", document.RootElement.GetProperty("status").GetString());
        var warning = Assert.Single(document.RootElement.GetProperty("warnings").EnumerateArray());
        Assert.Equal(link, warning.GetProperty("path").GetString());
        Assert.Contains("filesystem link", warning.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(0, document.RootElement.GetProperty("totals").GetProperty("estimatedBytes").GetInt64());
    }

    [Fact]
    public async Task Audit_reports_whitespace_only_unix_paths_without_an_uncaught_argument_failure()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var temporary = new TemporaryDirectory();
        var repository = await GitTestRepository.CreateAsync(temporary.GetPath("repo"));
        repository.Write(".gitignore", "*\n");
        await repository.GitAsync("add", "-f", ".gitignore");
        await repository.GitAsync("commit", "--quiet", "-m", "ignore all paths");
        repository.WriteBytes(" ", 3);
        repository.WriteBytes("\t", 5);
        repository.WriteBytes("\n", 7);

        var result = await RunAsync(["audit", repository.Path, "--min-size", "0", "--format", "json"]);

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.Stdout);
        Assert.Equal("success", document.RootElement.GetProperty("status").GetString());
        Assert.Equal(3, document.RootElement.GetProperty("totals").GetProperty("findingCount").GetInt64());
        Assert.Equal(15, document.RootElement.GetProperty("totals").GetProperty("estimatedBytes").GetInt64());
        Assert.Empty(document.RootElement.GetProperty("warnings").EnumerateArray());
    }

    private static async Task<GitTestRepository> CreateAuditRepositoryAsync(
        string path,
        string ignoredDirectory,
        int bytes)
    {
        var repository = await GitTestRepository.CreateAsync(path);
        repository.Write(".gitignore", $"{ignoredDirectory}/\n");
        repository.WriteBytes($"{ignoredDirectory}/payload.bin", bytes);
        await repository.CommitAllAsync();
        return repository;
    }

    private static async Task<AppResult> RunAsync(
        string[] arguments,
        string gitExecutable = "git",
        string? homeDirectory = null,
        CancellationToken cancellationToken = default)
    {
        using var input = new StringReader(string.Empty);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var runtime = new AppRuntime(
            gitExecutable,
            homeDirectory ?? Path.GetTempPath(),
            IsErrorInteractive: false);
        var exitCode = await RepoGleanApp.RunAsync(
            arguments,
            input,
            stdout,
            stderr,
            runtime,
            cancellationToken);
        return new AppResult(exitCode, stdout.ToString(), stderr.ToString());
    }

    private static void AssertAuditRootAndBytes(AppResult result, string expectedRoot, long expectedBytes)
    {
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.Stderr);
        using var document = JsonDocument.Parse(result.Stdout);
        Assert.Equal(expectedRoot, document.RootElement.GetProperty("effectiveRoots")[0].GetString());
        Assert.Equal(expectedBytes, document.RootElement.GetProperty("totals").GetProperty("estimatedBytes").GetInt64());
    }

    private static string[] FindingPaths(AppResult result)
    {
        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.Stdout);
        return document.RootElement.GetProperty("repositories")[0]
            .GetProperty("findings")
            .EnumerateArray()
            .Select(finding => finding.GetProperty("relativePath").GetString()!)
            .ToArray();
    }

    private static JsonElement ParseRoot(AppResult result)
    {
        using var document = JsonDocument.Parse(result.Stdout);
        return document.RootElement.Clone();
    }

    private static void SetSparseLength(string path, long length)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.SetLength(length);
    }

    private static IReadOnlyDictionary<string, string> SnapshotTree(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToDictionary(
                path => Path.GetRelativePath(root, path),
                path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
                StringComparer.Ordinal);

    private static void AssertContainsInOrder(string output, params string[] expectedValues)
    {
        var position = 0;
        foreach (var expectedValue in expectedValues)
        {
            var next = output.IndexOf(expectedValue, position, StringComparison.Ordinal);
            Assert.True(next >= position, $"Expected '{expectedValue}' after position {position} in:{Environment.NewLine}{output}");
            position = next + expectedValue.Length;
        }
    }

    private sealed record AppResult(int ExitCode, string Stdout, string Stderr);
}
