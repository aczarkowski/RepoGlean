using System.Text.Json;
using DevCleaner.Tests.Support;

namespace DevCleaner.Tests.Application;

public sealed class ReadOnlyCommandTests
{
    [Fact]
    public async Task No_arguments_prints_help_and_version_prints_a_stable_product_label()
    {
        var help = await RunAsync([]);
        var version = await RunAsync(["--version"]);

        Assert.Equal(0, help.ExitCode);
        Assert.Contains("Usage:", help.Stdout);
        Assert.Equal(string.Empty, help.Stderr);
        Assert.Equal(0, version.ExitCode);
        Assert.StartsWith("devcleaner ", version.Stdout);
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
    public async Task Scan_table_supports_details_quiet_verbose_and_never_colors_redirected_output()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await CreateRepositoryAsync(temporary.GetPath("repo"), 5);

        var detailed = await RunAsync(["scan", repository.Path, "--details", "--verbose"]);
        var quiet = await RunAsync(["scan", repository.Path, "--quiet"]);

        Assert.Equal(0, detailed.ExitCode);
        Assert.Contains("dotnet.obj", detailed.Stdout);
        Assert.DoesNotContain("\u001b[", detailed.Stdout, StringComparison.Ordinal);
        Assert.Equal(string.Empty, detailed.Stderr);
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
              {"id":"custom.generated","category":"Build","patterns":["**/.generated"],"markers":[],"preselected":true}
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
        var show = await RunAsync(["config", "show", "--format", "json", "--config", validPath]);
        var validate = await RunAsync(["config", "validate", "--config", validPath]);
        var invalid = await RunAsync(["scan", "--config", invalidPath], gitExecutable: "devcleaner-missing-git");

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
    public async Task Progress_uses_stderr_only_when_interactive_and_is_disabled_explicitly_or_for_json()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await CreateRepositoryAsync(temporary.GetPath("repo"), 5);

        var interactive = await RunAsync(["scan", repository.Path], isErrorInteractive: true);
        var disabled = await RunAsync(["scan", repository.Path, "--no-progress"], isErrorInteractive: true);
        var json = await RunAsync(["scan", repository.Path, "--format", "json"], isErrorInteractive: true);

        Assert.Contains("Scanning", interactive.Stderr);
        Assert.DoesNotContain("Scanning", interactive.Stdout);
        Assert.DoesNotContain("\u001b[", interactive.Stdout, StringComparison.Ordinal);
        Assert.Equal(string.Empty, disabled.Stderr);
        Assert.Equal(string.Empty, json.Stderr);
        JsonDocument.Parse(json.Stdout).Dispose();
    }

    [Fact]
    public async Task Scan_maps_missing_git_no_candidates_partial_warnings_usage_and_interruption_to_exact_exit_codes()
    {
        using var temporary = new TemporaryDirectory();
        var empty = await GitTestRepository.CreateAsync(temporary.GetPath("empty"));
        empty.Write("project.csproj", "<Project />");
        await empty.CommitAllAsync();
        var missingRoot = temporary.GetPath("missing");

        var missingGit = await RunAsync(["scan", empty.Path], gitExecutable: "devcleaner-missing-git");
        var noCandidates = await RunAsync(["scan", empty.Path]);
        var partial = await RunAsync(["scan", empty.Path, missingRoot, "--format", "json"]);
        var usage = await RunAsync(["scan", "--unknown"]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var interrupted = await RunAsync(["scan", empty.Path], cancellationToken: cancellation.Token);

        Assert.Equal(1, missingGit.ExitCode);
        Assert.Contains("Git executable", missingGit.Stderr);
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
        CancellationToken cancellationToken = default)
    {
        using var input = new StringReader(string.Empty);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var runtime = new AppRuntime(gitExecutable, Path.GetTempPath(), isErrorInteractive);
        var exitCode = await DevCleanerApp.RunAsync(arguments, input, stdout, stderr, runtime, cancellationToken);
        return new AppResult(exitCode, stdout.ToString(), stderr.ToString());
    }

    private sealed record AppResult(int ExitCode, string Stdout, string Stderr);
}
