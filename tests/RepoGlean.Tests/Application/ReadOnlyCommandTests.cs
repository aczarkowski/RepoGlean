using System.Text.Json;
using RepoGlean.Scanning;
using RepoGlean.Tests.Support;

namespace RepoGlean.Tests.Application;

public sealed class ReadOnlyCommandTests
{
    [Fact]
    public async Task No_arguments_prints_help_and_version_prints_a_stable_product_label()
    {
        var help = await RunAsync([]);
        var version = await RunAsync(["--version"]);

        Assert.Equal(0, help.ExitCode);
        Assert.Contains("Usage:", help.Stdout);
        Assert.Contains("repoglean audit", help.Stdout);
        Assert.Contains("Audit options:", help.Stdout);
        Assert.Equal(string.Empty, help.Stderr);
        Assert.Equal(0, version.ExitCode);
        Assert.Equal("repoglean 2.3.1", version.Stdout.Trim());
    }

    [Fact]
    public async Task Scan_json_uses_cli_roots_over_config_and_keeps_stdout_machine_clean()
    {
        using var temporary = new TemporaryDirectory();
        var configured = await CreateRepositoryAsync(temporary.GetPath("configured"), 4);
        var requested = await CreateRepositoryAsync(temporary.GetPath("requested"), 7);
        var configPath = temporary.GetPath("config.json");
        File.WriteAllText(configPath, $$"""{"schemaVersion":1,"roots":["{{JsonEncodedText.Encode(configured.Path)}}"]}""");

        var result = await RunAsync(["scan", requested.Path, "--format", "json", "--config", configPath, "--no-progress"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.Stderr);
        using var document = JsonDocument.Parse(result.Stdout);
        var root = document.RootElement;
        Assert.Equal(requested.Path, root.GetProperty("effectiveRoots")[0].GetString());
        Assert.Equal(7, root.GetProperty("totals").GetProperty("estimatedBytes").GetInt64());
        Assert.DoesNotContain(configured.Path, result.Stdout);
    }

    [Fact]
    public async Task Scan_uses_configured_roots_over_home_and_adds_cli_exclusions_to_config_exclusions()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await GitTestRepository.CreateAsync(temporary.GetPath("configured"));
        repository.Write("project.csproj", "<Project />");
        repository.Write(".gitignore", "obj/\nsrc/obj/\n");
        repository.WriteBytes("obj/first.bin", 3);
        repository.WriteBytes("src/obj/second.bin", 5);
        await repository.CommitAllAsync();
        var configPath = temporary.GetPath("config.json");
        File.WriteAllText(configPath, $$"""
            {"schemaVersion":1,"roots":["{{JsonEncodedText.Encode(repository.Path)}}"],"excludes":["obj"]}
            """);

        var result = await RunAsync(["scan", "--config", configPath, "--exclude", "src/obj", "--format", "json"]);

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.Stdout);
        var root = document.RootElement;
        Assert.Equal(repository.Path, root.GetProperty("effectiveRoots")[0].GetString());
        Assert.Equal(0, root.GetProperty("totals").GetProperty("candidateCount").GetInt64());
    }

    [Fact]
    public async Task Scan_all_drives_reports_the_final_expanded_roots_in_json_and_table_output()
    {
        using var temporary = new TemporaryDirectory();
        var requested = await CreateRepositoryAsync(temporary.GetPath("requested"), 3);
        var fixedDrive = await CreateRepositoryAsync(temporary.GetPath("fixed-drive"), 5);
        var driveRootProvider = new TestDriveRootProvider(fixedDrive.Path);

        var json = await RunAsync(
            ["scan", requested.Path, "--all-drives", "--format", "json"],
            driveRootProvider: driveRootProvider);
        var table = await RunAsync(
            ["scan", requested.Path, "--all-drives"],
            driveRootProvider: driveRootProvider);

        Assert.Equal(0, json.ExitCode);
        using var document = JsonDocument.Parse(json.Stdout);
        Assert.Equal(
            [requested.Path, fixedDrive.Path],
            document.RootElement.GetProperty("effectiveRoots").EnumerateArray().Select(root => root.GetString()));
        Assert.Contains($"Roots: {requested.Path}, {fixedDrive.Path}{Environment.NewLine}", table.Stdout);
    }

    [Fact]
    public async Task Scan_table_supports_details_quiet_verbose_and_never_colors_redirected_output()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await CreateRepositoryAsync(temporary.GetPath("repo"), 5);

        var detailed = await RunAsync(["scan", repository.Path, "--details", "--verbose"]);
        var quiet = await RunAsync(["scan", repository.Path, "--quiet"]);

        Assert.Equal(0, detailed.ExitCode);
        Assert.Contains("dotnet.obj", detailed.Stdout);
        Assert.DoesNotContain("\u001b[", detailed.Stdout, StringComparison.Ordinal);
        Assert.Contains("Discovering repositories", detailed.Stderr, StringComparison.Ordinal);
        Assert.Equal(0, quiet.ExitCode);
        Assert.Contains("Total", quiet.Stdout);
        Assert.DoesNotContain("dotnet.obj", quiet.Stdout);
    }

    [Fact]
    public async Task Rules_list_reports_built_in_and_custom_metadata()
    {
        using var temporary = new TemporaryDirectory();
        var configPath = temporary.GetPath("config.json");
        File.WriteAllText(configPath, """
            {"schemaVersion":1,"disabledRules":["dotnet.obj"],"customRules":[
              {"id":"custom.generated","category":"Build","patterns":["**/.generated"],"markers":[]}
            ]}
            """);

        var result = await RunAsync(["rules", "list", "--format", "json", "--config", configPath]);

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.Stdout);
        var rules = document.RootElement.GetProperty("rules").EnumerateArray().ToArray();
        Assert.Contains(rules, rule => rule.GetProperty("id").GetString() == "dotnet.obj" && !rule.GetProperty("enabled").GetBoolean() && rule.GetProperty("source").GetString() == "builtIn");
        Assert.Contains(rules, rule => rule.GetProperty("id").GetString() == "custom.generated" && rule.GetProperty("source").GetString() == "custom" && !rule.GetProperty("preselected").GetBoolean());
    }

    [Fact]
    public async Task Config_path_show_and_validate_are_read_only_and_validate_before_git_access()
    {
        using var temporary = new TemporaryDirectory();
        var validPath = temporary.GetPath("valid.json");
        var invalidPath = temporary.GetPath("invalid.json");
        File.WriteAllText(validPath, "{\"schemaVersion\":1,\"roots\":[\"example\"]}");
        File.WriteAllText(invalidPath, "{\"schemaVersion\":2}");

        var path = await RunAsync(["config", "path", "--config", validPath]);
        var show = await RunAsync(["config", "show", "--config", validPath]);
        var validate = await RunAsync(["config", "validate", "--config", validPath]);
        var invalid = await RunAsync(["scan", "--config", invalidPath], gitExecutable: "repoglean-missing-git");

        Assert.Equal(0, path.ExitCode);
        Assert.Equal(Path.GetFullPath(validPath), path.Stdout.Trim());
        Assert.Equal(0, show.ExitCode);
        Assert.Equal(1, JsonDocument.Parse(show.Stdout).RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(0, validate.ExitCode);
        Assert.Contains("valid", validate.Stdout, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, invalid.ExitCode);
        Assert.Contains("schemaVersion", invalid.Stderr);
        Assert.DoesNotContain("Git executable", invalid.Stderr);
    }

    [Fact]
    public async Task Explicit_missing_or_directory_config_paths_are_usage_failures_before_git_access()
    {
        using var temporary = new TemporaryDirectory();
        var missingPath = temporary.GetPath("missing.json");
        var directoryPath = temporary.GetPath("config-directory");
        Directory.CreateDirectory(directoryPath);

        var missing = await RunAsync(["scan", "--config", missingPath], gitExecutable: "repoglean-missing-git");
        var directory = await RunAsync(["scan", "--config", directoryPath], gitExecutable: "repoglean-missing-git");

        Assert.Equal(2, missing.ExitCode);
        Assert.Contains("Configuration error", missing.Stderr, StringComparison.Ordinal);
        Assert.Contains("does not exist", missing.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Git executable", missing.Stderr, StringComparison.Ordinal);
        Assert.Equal(2, directory.ExitCode);
        Assert.Contains("Configuration error", directory.Stderr, StringComparison.Ordinal);
        Assert.Contains("directory", directory.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Git executable", directory.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Config_show_preserves_camel_case_schema_and_string_categories()
    {
        using var temporary = new TemporaryDirectory();
        var configPath = temporary.GetPath("config.json");
        File.WriteAllText(configPath, """
            {"schemaVersion":1,"customRules":[
              {"id":"custom.generated","category":"Build","patterns":["**/.generated"]}
            ]}
            """);

        var result = await RunAsync(["config", "show", "--config", configPath]);

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.Stdout);
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("Build", root.GetProperty("customRules")[0].GetProperty("category").GetString());
    }

    [Fact]
    public async Task Scan_progress_selection_preserves_stdout_and_flag_precedence()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await CreateRepositoryAsync(temporary.GetPath("repo"), 5);

        var interactive = await RunAsync(["scan", repository.Path], isErrorInteractive: true);
        var redirected = await RunAsync(["scan", repository.Path], isErrorInteractive: false);
        var verboseRedirected = await RunAsync(
            ["scan", repository.Path, "--verbose"],
            isErrorInteractive: false);
        var verboseJson = await RunAsync(
            ["scan", repository.Path, "--verbose", "--format", "json"],
            isErrorInteractive: false);
        var verboseNoProgress = await RunAsync(
            ["scan", repository.Path, "--verbose", "--no-progress"],
            isErrorInteractive: false);
        var quietVerbose = await RunAsync(
            ["scan", repository.Path, "--quiet", "--verbose"],
            isErrorInteractive: true);

        Assert.Equal(0, interactive.ExitCode);
        Assert.Contains("Discovering repositories", interactive.Stderr, StringComparison.Ordinal);
        Assert.Contains("\r", interactive.Stderr, StringComparison.Ordinal);
        Assert.Contains("Roots:", interactive.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("Discovering repositories", interactive.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b", interactive.Stdout, StringComparison.Ordinal);

        Assert.Equal(0, redirected.ExitCode);
        Assert.Equal(string.Empty, redirected.Stderr);

        Assert.Equal(0, verboseRedirected.ExitCode);
        AssertContainsInOrder(
            verboseRedirected.Stderr,
            "Discovering repositories under",
            $"Scanning [1/1] {repository.Path}...",
            "Scan complete: 1 repository, 1 candidate, 0 warnings.");
        Assert.DoesNotContain("\r", verboseRedirected.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b", verboseRedirected.Stderr, StringComparison.Ordinal);

        Assert.Equal(0, verboseJson.ExitCode);
        using (var document = JsonDocument.Parse(verboseJson.Stdout))
        {
            Assert.Equal("scan", document.RootElement.GetProperty("operation").GetString());
        }

        Assert.Contains("Discovering repositories under", verboseJson.Stderr, StringComparison.Ordinal);
        Assert.Contains("Scan complete:", verboseJson.Stderr, StringComparison.Ordinal);

        Assert.Equal(0, verboseNoProgress.ExitCode);
        Assert.Contains("Discovering repositories under", verboseNoProgress.Stderr, StringComparison.Ordinal);
        Assert.Contains("Scan complete:", verboseNoProgress.Stderr, StringComparison.Ordinal);

        Assert.Equal(0, quietVerbose.ExitCode);
        Assert.Equal(string.Empty, quietVerbose.Stderr);
        Assert.StartsWith("Total ", quietVerbose.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("Roots:", quietVerbose.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Scan_progress_write_failures_do_not_change_the_report_or_exit_code()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await CreateRepositoryAsync(temporary.GetPath("repo"), 5);
        using var input = new StringReader(string.Empty);
        using var stdout = new StringWriter();
        using var stderr = new ProgressThrowingWriter();
        var runtime = new AppRuntime("git", Path.GetTempPath(), IsErrorInteractive: true);

        var exitCode = await RepoGleanApp.RunAsync(
            ["scan", repository.Path],
            input,
            stdout,
            stderr,
            runtime,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(1, stderr.ProgressWriteAttempts);
        Assert.Contains("Total 5 B estimated", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Verbose_scan_interruption_reports_a_milestone_before_the_existing_diagnostic()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await CreateRepositoryAsync(temporary.GetPath("repo"), 5);
        using var cancellation = new CancellationTokenSource();
        using var input = new StringReader(string.Empty);
        using var stdout = new StringWriter();
        using var stderr = new CancellingVerboseWriter(cancellation);
        var runtime = new AppRuntime("git", Path.GetTempPath(), IsErrorInteractive: false);

        var exitCode = await RepoGleanApp.RunAsync(
            ["scan", repository.Path, "--verbose"],
            input,
            stdout,
            stderr,
            runtime,
            cancellation.Token);

        Assert.Equal(130, exitCode);
        AssertContainsInOrder(
            stderr.ToString(),
            "Discovering repositories under",
            "Scan interrupted:",
            "Operation interrupted.");
    }

    [Fact]
    public async Task Verbose_scan_interruption_after_one_repository_keeps_factual_monotonic_totals()
    {
        using var temporary = new TemporaryDirectory();
        var first = await CreateRepositoryAsync(temporary.GetPath("first"), 5);
        _ = await CreateRepositoryAsync(temporary.GetPath("second"), 7);
        var missingRoot = temporary.GetPath("missing");
        using var cancellation = new CancellationTokenSource();
        using var input = new StringReader(string.Empty);
        using var stdout = new StringWriter();
        using var stderr = new CancellingVerboseWriter(
            cancellation,
            line => line.StartsWith(
                $"Found 1 candidate in {Path.GetFileName(first.Path)} ",
                StringComparison.Ordinal));
        var runtime = new AppRuntime("git", Path.GetTempPath(), IsErrorInteractive: false);

        var exitCode = await RepoGleanApp.RunAsync(
            ["scan", temporary.Path, missingRoot, "--verbose", "--format", "json"],
            input,
            stdout,
            stderr,
            runtime,
            cancellation.Token);

        Assert.Equal(130, exitCode);
        Assert.Contains(
            "Scan interrupted: 1 repository, 1 candidate, 1 warning.",
            stderr.ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain("Scan complete:", stderr.ToString(), StringComparison.Ordinal);
        using var document = JsonDocument.Parse(stdout.ToString());
        Assert.Equal("interrupted", document.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Json_scan_cancellation_after_completed_terminal_event_keeps_only_completed_and_valid_json()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await CreateRepositoryAsync(temporary.GetPath("repo"), 5);
        using var cancellation = new CancellationTokenSource();
        using var input = new StringReader(string.Empty);
        using var stdout = new StringWriter();
        using var stderr = new CancellingVerboseWriter(
            cancellation,
            line => line.StartsWith("Scan complete:", StringComparison.Ordinal));
        var runtime = new AppRuntime("git", Path.GetTempPath(), IsErrorInteractive: false);

        var exitCode = await RepoGleanApp.RunAsync(
            ["scan", repository.Path, "--verbose", "--format", "json"],
            input,
            stdout,
            stderr,
            runtime,
            cancellation.Token);

        Assert.Equal(0, exitCode);
        Assert.Equal(1, CountOccurrences(stderr.ToString(), "Scan complete:"));
        Assert.DoesNotContain("Scan interrupted:", stderr.ToString(), StringComparison.Ordinal);
        using var document = JsonDocument.Parse(stdout.ToString());
        Assert.Equal("success", document.RootElement.GetProperty("status").GetString());
        Assert.Equal(1, document.RootElement.GetProperty("totals").GetProperty("candidateCount").GetInt64());
    }

    [Fact]
    public async Task Public_RunAsync_never_colors_a_custom_stdout_even_when_the_process_console_is_interactive()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await CreateRepositoryAsync(temporary.GetPath("repo"), 5);
        using var input = new StringReader(string.Empty);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var simulatedRuntime = AppRuntime.Create(
            stdout,
            stderr,
            isOutputRedirected: false,
            isErrorRedirected: false);
        var exitCode = await RepoGleanApp.RunAsync(
            ["scan", repository.Path],
            input,
            stdout,
            stderr,
            CancellationToken.None);

        Assert.False(simulatedRuntime.IsOutputInteractive);
        Assert.Equal(0, exitCode);
        Assert.DoesNotContain("\u001b[", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Scan_maps_missing_git_no_candidates_partial_warnings_usage_and_interruption_to_exact_exit_codes()
    {
        using var temporary = new TemporaryDirectory();
        var empty = await GitTestRepository.CreateAsync(temporary.GetPath("empty"));
        empty.Write("project.csproj", "<Project />");
        await empty.CommitAllAsync();
        var missingRoot = temporary.GetPath("missing");

        var missingGit = await RunAsync(["scan", empty.Path], gitExecutable: "repoglean-missing-git");
        var verboseMissingGit = await RunAsync(
            ["scan", empty.Path, "--verbose"],
            gitExecutable: "repoglean-missing-git");
        var noCandidates = await RunAsync(["scan", empty.Path]);
        var partial = await RunAsync(["scan", empty.Path, missingRoot, "--format", "json"]);
        var usage = await RunAsync(["scan", "--unknown"]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var interrupted = await RunAsync(["scan", empty.Path], cancellationToken: cancellation.Token);

        Assert.Equal(1, missingGit.ExitCode);
        Assert.Contains("Git executable", missingGit.Stderr);
        Assert.Equal(1, verboseMissingGit.ExitCode);
        AssertContainsInOrder(
            verboseMissingGit.Stderr,
            "Scan failed: Git executable",
            "Error: Git executable");
        Assert.Equal(0, noCandidates.ExitCode);
        Assert.Contains("No candidates", noCandidates.Stdout);
        Assert.Equal(3, partial.ExitCode);
        Assert.Equal("partial", JsonDocument.Parse(partial.Stdout).RootElement.GetProperty("status").GetString());
        Assert.Equal(2, usage.ExitCode);
        Assert.Equal(130, interrupted.ExitCode);
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

    private static async Task<AppResult> RunAsync(
        string[] arguments,
        string gitExecutable = "git",
        bool isErrorInteractive = false,
        IDriveRootProvider? driveRootProvider = null,
        CancellationToken cancellationToken = default)
    {
        using var input = new StringReader(string.Empty);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var runtime = new AppRuntime(gitExecutable, Path.GetTempPath(), isErrorInteractive, DriveRootProvider: driveRootProvider);
        var exitCode = await RepoGleanApp.RunAsync(arguments, input, stdout, stderr, runtime, cancellationToken);
        return new AppResult(exitCode, stdout.ToString(), stderr.ToString());
    }

    private sealed record AppResult(int ExitCode, string Stdout, string Stderr);

    private static void AssertContainsInOrder(string output, params string[] expectedValues)
    {
        var position = 0;
        foreach (var expectedValue in expectedValues)
        {
            var next = output.IndexOf(expectedValue, position, StringComparison.Ordinal);
            Assert.True(
                next >= position,
                $"Expected '{expectedValue}' after position {position} in:{Environment.NewLine}{output}");
            position = next + expectedValue.Length;
        }
    }

    private static int CountOccurrences(string value, string expected)
    {
        var count = 0;
        var position = 0;
        while ((position = value.IndexOf(expected, position, StringComparison.Ordinal)) >= 0)
        {
            count++;
            position += expected.Length;
        }

        return count;
    }

    private sealed class ProgressThrowingWriter : StringWriter
    {
        public int ProgressWriteAttempts { get; private set; }

        public override void Write(string? value)
        {
            if (value?.StartsWith('\r') == true)
            {
                ProgressWriteAttempts++;
                throw new IOException("Simulated progress write failure.");
            }

            base.Write(value);
        }
    }

    private sealed class CancellingVerboseWriter(
        CancellationTokenSource cancellation,
        Func<string, bool>? shouldCancel = null) : StringWriter
    {
        public override void WriteLine(string? value)
        {
            base.WriteLine(value);
            if (value is not null &&
                (shouldCancel?.Invoke(value) ??
                 value.StartsWith("Discovering repositories under", StringComparison.Ordinal)))
            {
                cancellation.Cancel();
            }
        }
    }

    private sealed class TestDriveRootProvider(string root) : IDriveRootProvider
    {
        public DriveRootDiscoveryResult GetFixedDriveRoots() => new([root], []);
    }
}
