using System.Text.Json;
using DevCleaner.Tests.Support;

namespace DevCleaner.Tests.Application;

public sealed class CleanCommandTests
{
    [Fact]
    public async Task Interactive_defaults_select_all_repositories_but_only_preselected_artifacts()
    {
        using var temporary = new TemporaryDirectory();
        var first = await CreateRepositoryAsync(temporary.GetPath("first"));
        var second = await CreateRepositoryAsync(temporary.GetPath("second"));

        var result = await RunAsync(["clean", temporary.Path], "\n\ndelete\n");

        Assert.Equal(0, result.ExitCode);
        Assert.False(Directory.Exists(first.GetPath("obj")));
        Assert.False(Directory.Exists(second.GetPath("obj")));
        Assert.True(Directory.Exists(first.GetPath("node_modules")));
        Assert.True(Directory.Exists(second.GetPath("node_modules")));
    }

    [Fact]
    public async Task Interactive_all_is_the_explicit_dependency_opt_in()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await CreateRepositoryAsync(temporary.GetPath("repo"));

        var result = await RunAsync(["clean", repository.Path], "\nall\ndelete\n");

        Assert.Equal(0, result.ExitCode);
        Assert.False(Directory.Exists(repository.GetPath("obj")));
        Assert.False(Directory.Exists(repository.GetPath("node_modules")));
    }

    [Fact]
    public async Task Interactive_all_flag_makes_enter_include_dependencies()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await CreateRepositoryAsync(temporary.GetPath("repo"));

        var result = await RunAsync(["clean", repository.Path, "--all"], "\n\ndelete\n");

        Assert.Equal(0, result.ExitCode);
        Assert.False(Directory.Exists(repository.GetPath("obj")));
        Assert.False(Directory.Exists(repository.GetPath("node_modules")));
    }

    [Fact]
    public async Task Interactive_dependency_category_makes_enter_select_matching_dependencies()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await CreateRepositoryAsync(temporary.GetPath("repo"));

        var result = await RunAsync(["clean", repository.Path, "--category", "dependency"], "\n\ndelete\n");

        Assert.Equal(0, result.ExitCode);
        Assert.True(Directory.Exists(repository.GetPath("obj")));
        Assert.False(Directory.Exists(repository.GetPath("node_modules")));
    }

    [Theory]
    [InlineData("DELETE")]
    [InlineData("no")]
    [InlineData("")]
    public async Task Interactive_confirmation_requires_literal_lowercase_delete(string confirmation)
    {
        using var temporary = new TemporaryDirectory();
        var repository = await CreateRepositoryAsync(temporary.GetPath("repo"));

        var result = await RunAsync(["clean", repository.Path], $"\n\n{confirmation}\n");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("cancel", result.Stdout, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(repository.GetPath("obj")));
    }

    [Fact]
    public async Task Dry_run_is_noninteractive_and_preserves_all_selected_content()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await CreateRepositoryAsync(temporary.GetPath("repo"));

        var result = await RunAsync(["clean", repository.Path, "--dry-run", "--all"], string.Empty);

        Assert.Equal(0, result.ExitCode);
        Assert.True(Directory.Exists(repository.GetPath("obj")));
        Assert.True(Directory.Exists(repository.GetPath("node_modules")));
        Assert.Contains("dry run", result.Stdout, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Unattended_repository_scope_uses_default_preselection()
    {
        using var temporary = new TemporaryDirectory();
        var first = await CreateRepositoryAsync(temporary.GetPath("first"));
        var second = await CreateRepositoryAsync(temporary.GetPath("second"));

        var result = await RunAsync(["clean", temporary.Path, "--yes", "--repo", "first"], string.Empty);

        Assert.Equal(0, result.ExitCode);
        Assert.False(Directory.Exists(first.GetPath("obj")));
        Assert.True(Directory.Exists(first.GetPath("node_modules")));
        Assert.True(Directory.Exists(second.GetPath("obj")));
    }

    [Fact]
    public async Task Unattended_category_and_all_filters_opt_in_dependencies()
    {
        using var temporary = new TemporaryDirectory();
        var categoryRepository = await CreateRepositoryAsync(temporary.GetPath("category"));
        var allRepository = await CreateRepositoryAsync(temporary.GetPath("all"));

        var category = await RunAsync(["clean", categoryRepository.Path, "--yes", "--category", "dependency"], string.Empty);
        var all = await RunAsync(["clean", allRepository.Path, "--yes", "--all"], string.Empty);

        Assert.Equal(0, category.ExitCode);
        Assert.True(Directory.Exists(categoryRepository.GetPath("obj")));
        Assert.False(Directory.Exists(categoryRepository.GetPath("node_modules")));
        Assert.Equal(0, all.ExitCode);
        Assert.False(Directory.Exists(allRepository.GetPath("obj")));
        Assert.False(Directory.Exists(allRepository.GetPath("node_modules")));
    }

    [Fact]
    public async Task Json_cleanup_is_machine_clean_and_reports_exact_outcomes()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await CreateRepositoryAsync(temporary.GetPath("repo"));

        var result = await RunAsync(["clean", repository.Path, "--yes", "--all", "--format", "json"], string.Empty, isErrorInteractive: true);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.Stderr);
        using var document = JsonDocument.Parse(result.Stdout);
        var root = document.RootElement;
        Assert.Equal("clean", root.GetProperty("operation").GetString());
        Assert.Equal("success", root.GetProperty("status").GetString());
        Assert.Equal(2, root.GetProperty("cleanup").GetProperty("deletedCount").GetInt64());
        Assert.All(
            root.GetProperty("repositories").EnumerateArray().SelectMany(repositoryElement => repositoryElement.GetProperty("candidates").EnumerateArray()),
            candidate => Assert.Equal("deleted", candidate.GetProperty("outcome").GetString()));
    }

    [Fact]
    public async Task Human_partial_cleanup_uses_scan_style_quiet_and_verbose_diagnostics()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await CreateRepositoryAsync(temporary.GetPath("repo"));
        var missingRoot = temporary.GetPath("missing-root");

        var standard = await RunAsync(["clean", repository.Path, missingRoot, "--dry-run"], string.Empty);
        var verbose = await RunAsync(["clean", repository.Path, missingRoot, "--dry-run", "--verbose"], string.Empty);
        var quiet = await RunAsync(["clean", repository.Path, missingRoot, "--dry-run", "--quiet"], string.Empty);

        Assert.Equal(3, standard.ExitCode);
        Assert.Contains("Warnings: 1", standard.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(missingRoot, standard.Stdout, StringComparison.Ordinal);
        Assert.Equal(3, verbose.ExitCode);
        Assert.Contains("Warnings: 1", verbose.Stdout, StringComparison.Ordinal);
        Assert.Contains(missingRoot, verbose.Stdout, StringComparison.Ordinal);
        Assert.Equal(3, quiet.ExitCode);
        Assert.Contains("Dry run:", quiet.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("Warnings:", quiet.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(missingRoot, quiet.Stdout, StringComparison.Ordinal);
    }

    private static async Task<GitTestRepository> CreateRepositoryAsync(string path)
    {
        var repository = await GitTestRepository.CreateAsync(path);
        repository.Write("project.csproj", "<Project />");
        repository.Write("package.json", "{}");
        repository.Write(".gitignore", "obj/\nnode_modules/\n");
        repository.WriteBytes("obj/artifact.bin", 5);
        repository.WriteBytes("node_modules/package.bin", 7);
        await repository.CommitAllAsync();
        return repository;
    }

    private static async Task<AppResult> RunAsync(string[] arguments, string inputText, bool isErrorInteractive = false)
    {
        using var input = new StringReader(inputText);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var runtime = new AppRuntime("git", Path.GetTempPath(), isErrorInteractive);
        var exitCode = await DevCleanerApp.RunAsync(arguments, input, stdout, stderr, runtime, CancellationToken.None);
        return new AppResult(exitCode, stdout.ToString(), stderr.ToString());
    }

    private sealed record AppResult(int ExitCode, string Stdout, string Stderr);
}
