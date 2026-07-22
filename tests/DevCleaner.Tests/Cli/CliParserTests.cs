using DevCleaner.Cli;

namespace DevCleaner.Tests.Cli;

public sealed class CliParserTests
{
    [Fact]
    public void Parse_scan_accepts_roots_and_options()
    {
        var result = CliParser.Parse(["scan", "one", "two", "--repo", "api", "--category", "build", "--exclude", "generated", "--min-size", "1.5MiB", "--all-drives", "--details", "--dry-run"]);

        Assert.True(result.IsSuccess, result.Error);
        var options = Assert.IsType<CliOptions>(result.Value);
        Assert.Equal(CommandKind.Scan, options.Command);
        Assert.Equal(["one", "two"], options.Roots);
        Assert.Equal(["api"], options.Repositories);
        Assert.Equal([ArtifactCategory.Build], options.Categories);
        Assert.Equal(["generated"], options.Exclusions);
        Assert.Equal(1_572_864, options.MinimumBytes);
        Assert.True(options.AllDrives);
        Assert.True(options.Details);
        Assert.True(options.DryRun);
    }

    [Fact]
    public void Parse_clean_accepts_scoped_confirmation_and_roots()
    {
        var result = CliParser.Parse(["clean", "repo-root", "--repo", "api", "--yes", "--all"]);

        Assert.True(result.IsSuccess, result.Error);
        var options = Assert.IsType<CliOptions>(result.Value);
        Assert.Equal(CommandKind.Clean, options.Command);
        Assert.Equal(["repo-root"], options.Roots);
        Assert.Equal(["api"], options.Repositories);
        Assert.True(options.Yes);
        Assert.True(options.All);
    }

    [Fact]
    public void Parse_rules_list_succeeds()
    {
        var result = CliParser.Parse(["rules", "list"]);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(CommandKind.RulesList, result.Value!.Command);
    }

    [Theory]
    [InlineData("path", CommandKind.ConfigPath)]
    [InlineData("show", CommandKind.ConfigShow)]
    [InlineData("validate", CommandKind.ConfigValidate)]
    public void Parse_config_subcommands_succeed(string subcommand, CommandKind command)
    {
        var result = CliParser.Parse(["config", subcommand]);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(command, result.Value!.Command);
    }

    [Fact]
    public void Parse_accepts_global_flags_before_and_after_the_command()
    {
        var result = CliParser.Parse(["--format", "json", "--no-color", "scan", "root", "--details"]);

        Assert.True(result.IsSuccess, result.Error);
        var options = result.Value!;
        Assert.Equal(OutputFormat.Json, options.OutputFormat);
        Assert.True(options.NoColor);
        Assert.True(options.Details);
    }

    [Fact]
    public void Parse_collects_repeated_filters()
    {
        var result = CliParser.Parse(["scan", "--repo", "one", "--repo", "two", "--category", "build", "--category", "cache", "--exclude", "a", "--exclude", "b"]);

        Assert.True(result.IsSuccess, result.Error);
        var options = result.Value!;
        Assert.Equal(["one", "two"], options.Repositories);
        Assert.Equal([ArtifactCategory.Build, ArtifactCategory.Cache], options.Categories);
        Assert.Equal(["a", "b"], options.Exclusions);
    }

    [Fact]
    public void Parse_returns_usage_error_for_an_unknown_option()
    {
        var result = CliParser.Parse(["scan", "--wat"]);

        Assert.False(result.IsSuccess);
        Assert.Contains("Unknown option", result.Error);
    }

    [Theory]
    [InlineData("--repo")]
    [InlineData("--format")]
    [InlineData("--min-size")]
    public void Parse_returns_usage_error_for_a_missing_option_value(string option)
    {
        var result = CliParser.Parse(["scan", option]);

        Assert.False(result.IsSuccess);
        Assert.Contains("requires a value", result.Error);
    }

    [Fact]
    public void Parse_returns_usage_error_for_an_invalid_category()
    {
        var result = CliParser.Parse(["scan", "--category", "logs"]);

        Assert.False(result.IsSuccess);
        Assert.Contains("Invalid category", result.Error);
    }

    [Theory]
    [InlineData("clean", "--yes")]
    [InlineData("clean", "--yes", "--dry-run")]
    public void Parse_rejects_clean_yes_without_an_all_repo_or_category_scope(params string[] arguments)
    {
        var result = CliParser.Parse(arguments);

        Assert.False(result.IsSuccess);
        Assert.Contains("--yes", result.Error);
    }

    [Theory]
    [InlineData("clean", "--format", "json")]
    [InlineData("clean", "--format", "json", "--all")]
    public void Parse_rejects_json_clean_without_yes_or_dry_run(params string[] arguments)
    {
        var result = CliParser.Parse(arguments);

        Assert.False(result.IsSuccess);
        Assert.Contains("json", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("clean", "--format", "json", "--yes", "--all")]
    [InlineData("clean", "--format", "json", "--dry-run")]
    public void Parse_accepts_json_clean_with_yes_or_dry_run(params string[] arguments)
    {
        var result = CliParser.Parse(arguments);

        Assert.True(result.IsSuccess, result.Error);
    }
}
