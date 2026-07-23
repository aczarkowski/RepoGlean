using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RepoGlean.Tests.Support;
using Json.Schema;

namespace RepoGlean.Tests.Acceptance;

public sealed class EndToEndTests
{
    [Fact]
    public async Task Built_executable_keeps_scan_table_and_json_totals_in_parity()
    {
        using var temporary = new TemporaryDirectory();
        var first = await CreateRepositoryAsync(temporary.GetPath("first"), 17);
        var second = await CreateRepositoryAsync(temporary.GetPath("second"), 19);

        var table = await RunExecutableAsync(["scan", temporary.Path, "--details", "--no-progress"]);
        var json = await RunExecutableAsync(["scan", temporary.Path, "--format", "json", "--no-progress"]);

        Assert.Equal(0, table.ExitCode);
        Assert.Equal(string.Empty, table.Stderr);
        Assert.Equal(0, json.ExitCode);
        Assert.Equal(string.Empty, json.Stderr);
        using var document = JsonDocument.Parse(json.Stdout);
        var totals = document.RootElement.GetProperty("totals");
        Assert.Equal(2, totals.GetProperty("repositoryCount").GetInt64());
        Assert.Equal(2, totals.GetProperty("candidateCount").GetInt64());
        Assert.Equal(2, totals.GetProperty("fileCount").GetInt64());
        Assert.Equal(36, totals.GetProperty("estimatedBytes").GetInt64());
        Assert.Equal(new TableRepositoryTotals("19 B estimated", 1, 1, second.Path), ReadTableRepositoryTotals(table.Stdout, second.Path));
        Assert.Equal(new TableRepositoryTotals("17 B estimated", 1, 1, first.Path), ReadTableRepositoryTotals(table.Stdout, first.Path));
        Assert.Contains($"Total 36 B estimated | 2 candidates | 2 files | 2 repositories{Environment.NewLine}", table.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Built_executable_dry_run_matches_scan_and_preserves_content()
    {
        using var temporary = new TemporaryDirectory();
        var dryRunRepository = await CreateRepositoryWithBuildAndDependencyAsync(temporary.GetPath("dry-run"), 19, 23);
        var realCleanRepository = await CreateRepositoryWithBuildAndDependencyAsync(temporary.GetPath("real-clean"), 19, 23);

        var scan = await RunExecutableAsync(["scan", dryRunRepository.Path, "--format", "json", "--no-progress"]);
        var dryRun = await RunExecutableAsync(["clean", dryRunRepository.Path, "--dry-run", "--all", "--format", "json", "--no-progress"]);
        var realClean = await RunExecutableAsync(["clean", realCleanRepository.Path, "--yes", "--all", "--format", "json", "--no-progress"]);

        using var scanDocument = JsonDocument.Parse(scan.Stdout);
        using var dryRunDocument = JsonDocument.Parse(dryRun.Stdout);
        using var realCleanDocument = JsonDocument.Parse(realClean.Stdout);
        Assert.Equal(0, scan.ExitCode);
        Assert.Equal(0, dryRun.ExitCode);
        Assert.Equal(0, realClean.ExitCode);
        Assert.Equal(string.Empty, dryRun.Stderr);
        Assert.Equal(string.Empty, realClean.Stderr);
        var scanCandidates = ReadCandidateIdentities(scanDocument.RootElement);
        var dryRunCandidates = ReadCandidateIdentities(dryRunDocument.RootElement);
        var realCleanCandidates = ReadCandidateIdentities(realCleanDocument.RootElement);
        Assert.Equal(scanCandidates, dryRunCandidates);
        Assert.Equal(dryRunCandidates, realCleanCandidates);
        Assert.Equal(
            scanDocument.RootElement.GetProperty("totals").GetProperty("candidateCount").GetInt64(),
            dryRunDocument.RootElement.GetProperty("cleanup").GetProperty("selectedCount").GetInt64());
        Assert.Equal(
            dryRunDocument.RootElement.GetProperty("cleanup").GetProperty("selectedCount").GetInt64(),
            realCleanDocument.RootElement.GetProperty("cleanup").GetProperty("selectedCount").GetInt64());
        Assert.Equal(ReadTotals(scanDocument.RootElement), ReadTotals(dryRunDocument.RootElement));
        Assert.Equal(ReadTotals(dryRunDocument.RootElement), ReadTotals(realCleanDocument.RootElement));
        Assert.True(dryRunDocument.RootElement.GetProperty("cleanup").GetProperty("dryRun").GetBoolean());
        Assert.All(ReadCandidateOutcomes(dryRunDocument.RootElement), outcome => Assert.Equal("skipped", outcome));
        Assert.All(ReadCandidateOutcomes(realCleanDocument.RootElement), outcome => Assert.Equal("deleted", outcome));
        Assert.True(File.Exists(dryRunRepository.GetPath("obj/artifact.bin")));
        Assert.True(File.Exists(dryRunRepository.GetPath("node_modules/package.bin")));
        Assert.False(Directory.Exists(realCleanRepository.GetPath("obj")));
        Assert.False(Directory.Exists(realCleanRepository.GetPath("node_modules")));
    }

    [Fact]
    public async Task Built_executable_permanent_cleanup_removes_only_the_selected_ignored_artifact()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await CreateRepositoryAsync(temporary.GetPath("repo"), 23);
        repository.Write("package.json", "{}");
        repository.Write(".gitignore", "obj/\nnode_modules/\n");
        repository.WriteBytes("node_modules/package.bin", 29);
        repository.Write("tracked/keep.txt", "tracked");
        await repository.CommitAllAsync("add protected content");
        repository.Write("unrelated/keep.txt", "untracked but not ignored");

        var result = await RunExecutableAsync([
            "clean", repository.Path, "--yes", "--category", "build", "--format", "json", "--no-progress",
        ]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.Stderr);
        using var document = JsonDocument.Parse(result.Stdout);
        Assert.Equal("success", document.RootElement.GetProperty("status").GetString());
        Assert.Equal(1, document.RootElement.GetProperty("cleanup").GetProperty("deletedCount").GetInt64());
        Assert.False(Directory.Exists(repository.GetPath("obj")));
        Assert.True(File.Exists(repository.GetPath("node_modules/package.bin")));
        Assert.Equal("tracked", File.ReadAllText(repository.GetPath("tracked/keep.txt")));
        Assert.Equal("untracked but not ignored", File.ReadAllText(repository.GetPath("unrelated/keep.txt")));
        Assert.Empty(Directory.GetDirectories(repository.Path, ".repoglean-quarantine-*"));
    }

    [Fact]
    public async Task Built_executable_applies_cli_root_precedence_and_additive_exclusions()
    {
        using var temporary = new TemporaryDirectory();
        var configured = await CreateRepositoryAsync(temporary.GetPath("configured"), 29);
        var requested = await CreateRepositoryAsync(temporary.GetPath("requested"), 31);
        requested.WriteBytes("src/obj/second.bin", 37);
        requested.Write(".gitignore", "obj/\nsrc/obj/\n");
        await requested.CommitAllAsync("add second ignored output");
        var configPath = temporary.GetPath("config.json");
        await File.WriteAllTextAsync(configPath, $$"""
            {"schemaVersion":1,"roots":["{{JsonEncodedText.Encode(configured.Path)}}"],"excludes":["obj"]}
            """);

        var result = await RunExecutableAsync([
            "scan", requested.Path, "--config", configPath, "--exclude", "src/obj", "--format", "json", "--no-progress",
        ]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.Stderr);
        using var document = JsonDocument.Parse(result.Stdout);
        Assert.Equal(requested.Path, document.RootElement.GetProperty("effectiveRoots")[0].GetString());
        Assert.Equal(0, document.RootElement.GetProperty("totals").GetProperty("candidateCount").GetInt64());
        Assert.DoesNotContain(configured.Path, result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Built_executable_keeps_json_stdout_clean_and_maps_public_exit_codes()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await CreateRepositoryAsync(temporary.GetPath("repo"), 41);
        var missingRoot = temporary.GetPath("missing");

        var success = await RunExecutableAsync(["scan", repository.Path, "--format", "json", "--no-progress"]);
        var partial = await RunExecutableAsync(["scan", repository.Path, missingRoot, "--format", "json", "--no-progress"]);
        var usage = await RunExecutableAsync(["scan", "--unknown", "--format", "json"]);
        var missingGit = await RunExecutableAsync(
            ["scan", repository.Path, "--format", "json", "--no-progress"],
            new Dictionary<string, string?> { ["PATH"] = string.Empty });

        Assert.Equal(0, success.ExitCode);
        Assert.Equal(string.Empty, success.Stderr);
        Assert.Equal("success", JsonDocument.Parse(success.Stdout).RootElement.GetProperty("status").GetString());
        Assert.Equal(3, partial.ExitCode);
        Assert.Equal(string.Empty, partial.Stderr);
        Assert.Equal("partial", JsonDocument.Parse(partial.Stdout).RootElement.GetProperty("status").GetString());
        Assert.Equal(2, usage.ExitCode);
        Assert.Equal(string.Empty, usage.Stdout);
        Assert.Contains("Unknown option", usage.Stderr, StringComparison.Ordinal);
        Assert.Equal(1, missingGit.ExitCode);
        Assert.Equal(string.Empty, missingGit.Stderr);
        using var missingGitDocument = JsonDocument.Parse(missingGit.Stdout);
        Assert.Equal("failed", missingGitDocument.RootElement.GetProperty("status").GetString());
        Assert.Contains(
            "Git executable",
            missingGitDocument.RootElement.GetProperty("errors")[0].GetProperty("message").GetString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Portable_pre_cancelled_json_operation_returns_130_without_console_noise()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await CreateRepositoryAsync(temporary.GetPath("repo"), 43);
        using var input = new StringReader(string.Empty);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var exitCode = await RepoGleanApp.RunAsync(
            ["scan", repository.Path, "--format", "json", "--no-progress"],
            input,
            stdout,
            stderr,
            cancellation.Token);

        Assert.Equal(130, exitCode);
        Assert.Equal(string.Empty, stderr.ToString());
        using var document = JsonDocument.Parse(stdout.ToString());
        Assert.Equal("interrupted", document.RootElement.GetProperty("status").GetString());
        Assert.Equal("Operation interrupted.", document.RootElement.GetProperty("errors")[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task Process_helper_drains_large_stdout_and_stderr_concurrently()
    {
        var executable = OperatingSystem.IsWindows() ? "pwsh" : "/bin/sh";
        IReadOnlyList<string> arguments = OperatingSystem.IsWindows()
            ? ["-NoProfile", "-Command", "[Console]::Out.Write(('o' * 262144)); [Console]::Error.Write(('e' * 262144))"]
            : ["-c", "head -c 262144 /dev/zero; head -c 262144 /dev/zero >&2"];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var result = await RunProcessAsync(executable, arguments, cancellationToken: timeout.Token);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(262144, result.Stdout.Length);
        Assert.Equal(262144, result.Stderr.Length);
    }

    [Fact]
    public async Task Release_surface_documents_help_schema_and_all_native_targets()
    {
        var repositoryRoot = FindRepositoryRoot();
        var executableHelp = await RunExecutableAsync(["--help"]);
        var readmePath = Path.Combine(repositoryRoot, "README.md");
        var schemaPath = Path.Combine(repositoryRoot, "docs", "configuration.schema.json");
        var ciPath = Path.Combine(repositoryRoot, ".github", "workflows", "ci.yml");
        var releasePath = Path.Combine(repositoryRoot, ".github", "workflows", "release.yml");
        var smokePath = Path.Combine(repositoryRoot, "eng", "native-smoke.ps1");

        Assert.Equal(0, executableHelp.ExitCode);
        Assert.Contains("--config", executableHelp.Stdout, StringComparison.Ordinal);
        Assert.Contains("--no-progress", executableHelp.Stdout, StringComparison.Ordinal);
        Assert.True(File.Exists(readmePath), "README.md must document the supported RepoGlean surface.");
        Assert.True(File.Exists(schemaPath), "The configuration JSON Schema must be published.");
        Assert.True(File.Exists(ciPath), "The cross-platform CI workflow must be published.");
        Assert.True(File.Exists(releasePath), "The Native AOT release workflow must be published.");
        Assert.True(File.Exists(smokePath), "The reusable packaged-executable smoke script must be published.");

        var schema = JsonNode.Parse(await File.ReadAllTextAsync(schemaPath))!.AsObject();
        Assert.Equal("https://json-schema.org/draft/2020-12/schema", schema["$schema"]!.GetValue<string>());
        Assert.Equal(1, schema["$defs"]!["schemaVersion"]!["const"]!.GetValue<int>());

        var readme = await File.ReadAllTextAsync(readmePath);
        Assert.Contains("glibc", readme, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("STATX_MNT_ID", readme, StringComparison.Ordinal);
        Assert.Contains("Ubuntu 24.04", readme, StringComparison.Ordinal);
        Assert.Contains("ext4", readme, StringComparison.OrdinalIgnoreCase);

        var smoke = await File.ReadAllTextAsync(smokePath);
        Assert.Contains("ConvertFrom-Json", smoke, StringComparison.Ordinal);
        Assert.Contains("clean", smoke, StringComparison.OrdinalIgnoreCase);

        var ci = await File.ReadAllTextAsync(ciPath);
        var release = await File.ReadAllTextAsync(releasePath);
        Assert.Contains("eng/native-smoke.ps1", ci, StringComparison.Ordinal);
        Assert.Contains("artifacts/package", ci, StringComparison.Ordinal);
        Assert.Contains("eng/native-smoke.ps1", release, StringComparison.Ordinal);
        Assert.True(
            release.IndexOf("Prepare release package", StringComparison.Ordinal) <
            release.IndexOf("Smoke-test packaged executable", StringComparison.Ordinal),
            "Release smoke must run after the final executable name is prepared.");
        foreach (var rid in new[] { "win-x64", "win-arm64", "osx-x64", "osx-arm64", "linux-x64", "linux-arm64" })
        {
            Assert.Contains(rid, release, StringComparison.Ordinal);
        }
        Assert.Contains("GH_REPO: ${{ github.repository }}", release, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(ConfigurationContractSamples.All), MemberType = typeof(ConfigurationContractSamples))]
    public async Task Published_schema_and_config_validate_accept_the_same_contract_samples(string name, string json, bool expectedValid)
    {
        using var temporary = new TemporaryDirectory();
        var configPath = temporary.GetPath("config.json");
        await File.WriteAllTextAsync(configPath, json);
        var schemaPath = Path.Combine(FindRepositoryRoot(), "docs", "configuration.schema.json");
        using var schemaDocument = JsonDocument.Parse(await File.ReadAllTextAsync(schemaPath));
        using var instanceDocument = JsonDocument.Parse(json);
        var schema = JsonSchema.Build(schemaDocument.RootElement);

        var schemaValid = schema.Evaluate(instanceDocument.RootElement).IsValid;
        var executable = await RunExecutableAsync(["config", "validate", "--config", configPath]);

        Assert.True(schemaValid == expectedValid, $"Schema result mismatch for '{name}'.");
        Assert.True(executable.ExitCode == (expectedValid ? 0 : 2), $"Executable result mismatch for '{name}': {executable.Stderr}");
    }

    private static async Task<GitTestRepository> CreateRepositoryAsync(string path, int artifactBytes)
    {
        var repository = await GitTestRepository.CreateAsync(path);
        repository.Write("project.csproj", "<Project />");
        repository.Write(".gitignore", "obj/\n");
        repository.WriteBytes("obj/artifact.bin", artifactBytes);
        await repository.CommitAllAsync();
        return repository;
    }

    private static async Task<GitTestRepository> CreateRepositoryWithBuildAndDependencyAsync(
        string path,
        int buildBytes,
        int dependencyBytes)
    {
        var repository = await GitTestRepository.CreateAsync(path);
        repository.Write("project.csproj", "<Project />");
        repository.Write("package.json", "{}");
        repository.Write(".gitignore", "obj/\nnode_modules/\n");
        repository.WriteBytes("obj/artifact.bin", buildBytes);
        repository.WriteBytes("node_modules/package.bin", dependencyBytes);
        await repository.CommitAllAsync();
        return repository;
    }

    private static IReadOnlyList<CandidateIdentity> ReadCandidateIdentities(JsonElement report) => report
        .GetProperty("repositories")
        .EnumerateArray()
        .SelectMany(repository => repository.GetProperty("candidates").EnumerateArray())
        .Select(candidate => new CandidateIdentity(
            candidate.GetProperty("relativePath").GetString()!,
            candidate.GetProperty("ruleId").GetString()!,
            candidate.GetProperty("category").GetString()!,
            candidate.GetProperty("preselected").GetBoolean(),
            candidate.GetProperty("fileCount").GetInt64(),
            candidate.GetProperty("estimatedBytes").GetInt64()))
        .OrderBy(candidate => candidate.RelativePath, StringComparer.Ordinal)
        .ToArray();

    private static IReadOnlyList<string> ReadCandidateOutcomes(JsonElement report) => report
        .GetProperty("repositories")
        .EnumerateArray()
        .SelectMany(repository => repository.GetProperty("candidates").EnumerateArray())
        .Select(candidate => candidate.GetProperty("outcome").GetString()!)
        .ToArray();

    private static ReportTotals ReadTotals(JsonElement report)
    {
        var totals = report.GetProperty("totals");
        return new ReportTotals(
            totals.GetProperty("repositoryCount").GetInt64(),
            totals.GetProperty("candidateCount").GetInt64(),
            totals.GetProperty("fileCount").GetInt64(),
            totals.GetProperty("estimatedBytes").GetInt64());
    }

    private static TableRepositoryTotals ReadTableRepositoryTotals(string output, string repositoryPath)
    {
        var line = output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Single(value => value.EndsWith(repositoryPath, StringComparison.Ordinal));
        var match = Regex.Match(
            line,
            "^(?<bytes>.+ estimated)\\s+(?<candidates>[0-9]+)\\s+(?<files>[0-9]+)\\s+(?<repository>.+)$",
            RegexOptions.CultureInvariant);
        Assert.True(match.Success, $"Could not parse repository totals row: {line}");
        return new TableRepositoryTotals(
            match.Groups["bytes"].Value,
            int.Parse(match.Groups["candidates"].Value, System.Globalization.CultureInfo.InvariantCulture),
            long.Parse(match.Groups["files"].Value, System.Globalization.CultureInfo.InvariantCulture),
            match.Groups["repository"].Value);
    }

    private static async Task<ProcessResult> RunExecutableAsync(
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?>? environment = null) =>
        await RunProcessAsync(FindBuiltExecutable(), arguments, environment);

    private static async Task<ProcessResult> RunProcessAsync(
        string executable,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?>? environment = null,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = FindRepositoryRoot(),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        if (environment is not null)
        {
            foreach (var pair in environment) startInfo.Environment[pair.Key] = pair.Value;
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start process '{executable}'.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(stdoutTask, stderrTask);
            return new ProcessResult(process.ExitCode, stdoutTask.Result, stderrTask.Result);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
    }

    private static string FindBuiltExecutable()
    {
        var executableName = OperatingSystem.IsWindows() ? "RepoGlean.exe" : "RepoGlean";
        var copiedAppHost = Path.Combine(AppContext.BaseDirectory, executableName);
        if (File.Exists(copiedAppHost)) return copiedAppHost;

        throw new FileNotFoundException("The RepoGlean apphost was not copied to the test output.", copiedAppHost);
    }

    private static string FindRepositoryRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "RepoGlean.slnx"))) return current.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate the RepoGlean repository root.");
    }

    private sealed record CandidateIdentity(
        string RelativePath,
        string RuleId,
        string Category,
        bool Preselected,
        long FileCount,
        long EstimatedBytes);

    private sealed record ReportTotals(long RepositoryCount, long CandidateCount, long FileCount, long EstimatedBytes);

    private sealed record TableRepositoryTotals(string EstimatedBytes, int CandidateCount, long FileCount, string RepositoryPath);

    private sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);
}
