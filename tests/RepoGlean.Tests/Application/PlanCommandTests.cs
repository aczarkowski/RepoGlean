using System.Text.Json;
using RepoGlean.Tests.Support;

namespace RepoGlean.Tests.Application;

public sealed class PlanCommandTests
{
    private static readonly DateTimeOffset ReferenceTime =
        new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Plan_json_selects_only_the_balanced_prefix_and_is_read_only()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await CreatePlanningRepositoryAsync(temporary.GetPath("repo"));

        var result = await RunAsync(
            ["plan", repository.Path, "--free", "4B", "--format", "json", "--no-progress"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.Stderr);
        using var document = JsonDocument.Parse(result.Stdout);
        var root = document.RootElement;
        Assert.Equal("plan", root.GetProperty("operation").GetString());
        Assert.True(root.GetProperty("plan").GetProperty("targetMet").GetBoolean());
        Assert.Equal(
            "TestResults",
            root.GetProperty("plan").GetProperty("selectedCandidates")[0].GetProperty("relativePath").GetString());
        Assert.True(Directory.Exists(repository.GetPath("TestResults")));
        Assert.True(Directory.Exists(repository.GetPath("obj")));
    }

    [Fact]
    public async Task Plan_shortfall_is_a_valid_partial_result()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await CreatePlanningRepositoryAsync(temporary.GetPath("repo"));

        var result = await RunAsync(
            ["plan", repository.Path, "--free", "1TiB", "--format", "json"]);

        Assert.Equal(3, result.ExitCode);
        using var document = JsonDocument.Parse(result.Stdout);
        Assert.Equal("partial", document.RootElement.GetProperty("status").GetString());
        Assert.True(document.RootElement.GetProperty("plan").GetProperty("shortfallBytes").GetInt64() > 0);
    }

    [Fact]
    public async Task Plan_applies_repository_category_exclusion_and_minimum_size_filters()
    {
        using var temporary = new TemporaryDirectory();
        var selected = await CreatePlanningRepositoryAsync(temporary.GetPath("selected"));
        var other = await CreatePlanningRepositoryAsync(temporary.GetPath("other"));

        var result = await RunAsync(
        [
            "plan",
            temporary.Path,
            "--free",
            "6B",
            "--repo",
            "selected",
            "--category",
            "build",
            "--exclude",
            "TestResults",
            "--min-size",
            "6B",
            "--format",
            "json",
        ]);

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.Stdout);
        var plan = document.RootElement.GetProperty("plan");
        var candidate = Assert.Single(plan.GetProperty("selectedCandidates").EnumerateArray());
        Assert.Equal(selected.Path, candidate.GetProperty("repositoryRoot").GetString());
        Assert.Equal("obj", candidate.GetProperty("relativePath").GetString());
        Assert.DoesNotContain(other.Path, result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Plan_excludes_dependencies_by_default_and_includes_them_only_when_opted_in()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await CreatePlanningRepositoryAsync(temporary.GetPath("repo"));

        var defaultResult = await RunAsync(
            ["plan", repository.Path, "--free", "100B", "--format", "json"]);
        var allResult = await RunAsync(
            ["plan", repository.Path, "--free", "19B", "--all", "--format", "json"]);

        Assert.Equal(3, defaultResult.ExitCode);
        using (var document = JsonDocument.Parse(defaultResult.Stdout))
        {
            var candidates = document.RootElement
                .GetProperty("plan")
                .GetProperty("selectedCandidates")
                .EnumerateArray()
                .ToArray();
            Assert.DoesNotContain(
                candidates,
                candidate => candidate.GetProperty("category").GetString() == "dependency");
        }

        Assert.Equal(0, allResult.ExitCode);
        using (var document = JsonDocument.Parse(allResult.Stdout))
        {
            var candidates = document.RootElement
                .GetProperty("plan")
                .GetProperty("selectedCandidates")
                .EnumerateArray()
                .ToArray();
            Assert.Contains(
                candidates,
                candidate => candidate.GetProperty("relativePath").GetString() == "node_modules");
        }
    }

    [Fact]
    public async Task Plan_uses_configured_roots_when_no_command_line_root_is_given()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await CreatePlanningRepositoryAsync(temporary.GetPath("configured"));
        var configPath = temporary.GetPath("config.json");
        File.WriteAllText(
            configPath,
            JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                roots = new[] { repository.Path },
            }));

        var result = await RunAsync(
            ["plan", "--free", "4B", "--format", "json", "--config", configPath]);

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.Stdout);
        Assert.Equal(
            repository.Path,
            document.RootElement.GetProperty("effectiveRoots")[0].GetString());
    }

    [Fact]
    public async Task Plan_reports_an_empty_eligible_pool_without_mutating_the_repository()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await GitTestRepository.CreateAsync(temporary.GetPath("empty"));
        repository.Write("project.csproj", "<Project />");
        await repository.CommitAllAsync();

        var result = await RunAsync(["plan", repository.Path, "--free", "1B"]);

        Assert.Equal(3, result.ExitCode);
        Assert.Contains("Eligible pool: 0 B estimated", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("Shortfall: 1 B estimated", result.Stdout, StringComparison.Ordinal);
        Assert.True(File.Exists(repository.GetPath("project.csproj")));
    }

    [Fact]
    public async Task Json_plan_cancellation_names_the_interrupted_operation_and_keeps_stderr_clean()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await CreatePlanningRepositoryAsync(temporary.GetPath("repo"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await RunAsync(
            ["plan", repository.Path, "--free", "4B", "--format", "json"],
            cancellationToken: cancellation.Token);

        Assert.Equal(130, result.ExitCode);
        Assert.Equal(string.Empty, result.Stderr);
        using var document = JsonDocument.Parse(result.Stdout);
        Assert.Equal("plan", document.RootElement.GetProperty("operation").GetString());
        Assert.Equal("interrupted", document.RootElement.GetProperty("status").GetString());
        Assert.True(Directory.Exists(repository.GetPath("TestResults")));
    }

    [Fact]
    public async Task Help_documents_plan_usage_and_its_required_reclaim_target()
    {
        var result = await RunAsync(["help"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("repoglean plan [root ...] --free size [options]", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("Plan options:", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("--free", result.Stdout, StringComparison.Ordinal);
    }

    private static async Task<GitTestRepository> CreatePlanningRepositoryAsync(
        string path)
    {
        var repository = await GitTestRepository.CreateAsync(path);
        repository.Write("project.csproj", "<Project />");
        repository.Write("build.gradle", "plugins {}");
        repository.Write("package.json", "{}");
        repository.Write(
            ".gitignore",
            "TestResults/\nobj/\n.gradle/\nnode_modules/\n");
        repository.WriteBytes("TestResults/result.bin", 4);
        repository.WriteBytes("obj/artifact.bin", 6);
        repository.WriteBytes(".gradle/cache.bin", 8);
        repository.WriteBytes("node_modules/package.bin", 10);
        await repository.CommitAllAsync();
        Directory.SetLastWriteTimeUtc(
            repository.GetPath("TestResults"),
            ReferenceTime.AddDays(-40).UtcDateTime);
        File.SetLastWriteTimeUtc(
            repository.GetPath("TestResults/result.bin"),
            ReferenceTime.AddDays(-40).UtcDateTime);
        return repository;
    }

    private static async Task<AppResult> RunAsync(
        string[] arguments,
        string inputText = "",
        bool isErrorInteractive = false,
        CancellationToken cancellationToken = default)
    {
        using var input = new StringReader(inputText);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var runtime = new AppRuntime(
            "git",
            Path.GetTempPath(),
            isErrorInteractive,
            UtcNowProvider: () => ReferenceTime);
        var exitCode = await RepoGleanApp.RunAsync(
            arguments,
            input,
            stdout,
            stderr,
            runtime,
            cancellationToken);
        return new AppResult(exitCode, stdout.ToString(), stderr.ToString());
    }

    private sealed record AppResult(int ExitCode, string Stdout, string Stderr);
}
