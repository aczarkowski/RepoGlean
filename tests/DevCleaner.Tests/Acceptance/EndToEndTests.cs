using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using DevCleaner.Tests.Support;

namespace DevCleaner.Tests.Acceptance;

public sealed class EndToEndTests
{
    [Fact]
    public async Task Built_executable_keeps_scan_table_and_json_totals_in_parity()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await CreateRepositoryAsync(temporary.GetPath("repo"), 17);

        var table = await RunExecutableAsync(["scan", repository.Path, "--details", "--no-progress"]);
        var json = await RunExecutableAsync(["scan", repository.Path, "--format", "json", "--no-progress"]);

        Assert.Equal(0, table.ExitCode);
        Assert.Equal(string.Empty, table.Stderr);
        Assert.Contains("17 B estimated", table.Stdout, StringComparison.Ordinal);
        Assert.Contains("obj", table.Stdout, StringComparison.Ordinal);
        Assert.Equal(0, json.ExitCode);
        Assert.Equal(string.Empty, json.Stderr);
        using var document = JsonDocument.Parse(json.Stdout);
        Assert.Equal(1, document.RootElement.GetProperty("totals").GetProperty("candidateCount").GetInt64());
        Assert.Equal(1, document.RootElement.GetProperty("totals").GetProperty("fileCount").GetInt64());
        Assert.Equal(17, document.RootElement.GetProperty("totals").GetProperty("estimatedBytes").GetInt64());
    }

    [Fact]
    public async Task Built_executable_dry_run_matches_scan_and_preserves_content()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await CreateRepositoryAsync(temporary.GetPath("repo"), 19);

        var scan = await RunExecutableAsync(["scan", repository.Path, "--format", "json", "--no-progress"]);
        var dryRun = await RunExecutableAsync(["clean", repository.Path, "--dry-run", "--format", "json", "--no-progress"]);

        using var scanDocument = JsonDocument.Parse(scan.Stdout);
        using var cleanDocument = JsonDocument.Parse(dryRun.Stdout);
        Assert.Equal(0, scan.ExitCode);
        Assert.Equal(0, dryRun.ExitCode);
        Assert.Equal(string.Empty, dryRun.Stderr);
        Assert.Equal(
            scanDocument.RootElement.GetProperty("totals").GetProperty("candidateCount").GetInt64(),
            cleanDocument.RootElement.GetProperty("cleanup").GetProperty("selectedCount").GetInt64());
        Assert.Equal(
            scanDocument.RootElement.GetProperty("totals").GetProperty("estimatedBytes").GetInt64(),
            cleanDocument.RootElement.GetProperty("totals").GetProperty("estimatedBytes").GetInt64());
        Assert.True(cleanDocument.RootElement.GetProperty("cleanup").GetProperty("dryRun").GetBoolean());
        Assert.True(File.Exists(repository.GetPath("obj/artifact.bin")));
    }

    [Fact]
    public async Task Built_executable_permanent_cleanup_removes_only_the_selected_ignored_artifact()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await CreateRepositoryAsync(temporary.GetPath("repo"), 23);
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
        Assert.Equal("tracked", File.ReadAllText(repository.GetPath("tracked/keep.txt")));
        Assert.Equal("untracked but not ignored", File.ReadAllText(repository.GetPath("unrelated/keep.txt")));
        Assert.Empty(Directory.GetDirectories(repository.Path, ".devcleaner-quarantine-*"));
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

        var exitCode = await DevCleanerApp.RunAsync(
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
    public async Task Release_surface_documents_help_schema_and_all_native_targets()
    {
        var repositoryRoot = FindRepositoryRoot();
        var executableHelp = await RunExecutableAsync(["--help"]);
        var readmePath = Path.Combine(repositoryRoot, "README.md");
        var schemaPath = Path.Combine(repositoryRoot, "docs", "configuration.schema.json");
        var ciPath = Path.Combine(repositoryRoot, ".github", "workflows", "ci.yml");
        var releasePath = Path.Combine(repositoryRoot, ".github", "workflows", "release.yml");

        Assert.Equal(0, executableHelp.ExitCode);
        Assert.Contains("--config", executableHelp.Stdout, StringComparison.Ordinal);
        Assert.Contains("--no-progress", executableHelp.Stdout, StringComparison.Ordinal);
        Assert.True(File.Exists(readmePath), "README.md must document the supported v1 surface.");
        Assert.True(File.Exists(schemaPath), "The v1 configuration JSON Schema must be published.");
        Assert.True(File.Exists(ciPath), "The cross-platform CI workflow must be published.");
        Assert.True(File.Exists(releasePath), "The Native AOT release workflow must be published.");

        var schema = JsonNode.Parse(await File.ReadAllTextAsync(schemaPath))!.AsObject();
        Assert.Equal("https://json-schema.org/draft/2020-12/schema", schema["$schema"]!.GetValue<string>());
        Assert.Equal(1, schema["properties"]!["schemaVersion"]!["const"]!.GetValue<int>());

        var release = await File.ReadAllTextAsync(releasePath);
        foreach (var rid in new[] { "win-x64", "win-arm64", "osx-x64", "osx-arm64", "linux-x64", "linux-arm64" })
        {
            Assert.Contains(rid, release, StringComparison.Ordinal);
        }
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

    private static async Task<ProcessResult> RunExecutableAsync(
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = FindBuiltExecutable(),
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

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the built DevCleaner executable.");
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new ProcessResult(process.ExitCode, stdout, stderr);
    }

    private static string FindBuiltExecutable()
    {
        var executableName = OperatingSystem.IsWindows() ? "DevCleaner.exe" : "DevCleaner";
        var copiedAppHost = Path.Combine(AppContext.BaseDirectory, executableName);
        if (File.Exists(copiedAppHost)) return copiedAppHost;

        throw new FileNotFoundException("The DevCleaner apphost was not copied to the test output.", copiedAppHost);
    }

    private static string FindRepositoryRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "DevCleaner.slnx"))) return current.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate the DevCleaner repository root.");
    }

    private sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);
}
