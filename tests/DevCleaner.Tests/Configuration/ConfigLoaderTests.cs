using DevCleaner.Cli;
using DevCleaner.Configuration;
using DevCleaner.Tests.Support;

namespace DevCleaner.Tests.Configuration;

public sealed class ConfigLoaderTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"devcleaner-tests-{Guid.NewGuid():N}");

    [Fact]
    public void Load_accepts_comments_trailing_commas_and_string_categories()
    {
        Directory.CreateDirectory(directory);
        var path = Write("""
            {
              // developer machine cleanup policy
              "schemaVersion": 1,
              "roots": ["/work",],
              "excludes": ["**/generated/**",],
              "disabledRules": ["node.node-modules",],
              "customRules": [{
                "id": "company.generated",
                "category": "Build",
                "patterns": ["**/generated/**",],
                "markers": ["**/*.csproj",],
              },],
            }
            """);

        var result = ConfigLoader.Load(path);

        Assert.True(result.IsSuccess, result.Error);
        var config = Assert.IsType<DevCleanerConfig>(result.Config);
        Assert.Equal(1, config.SchemaVersion);
        Assert.Equal(["/work"], config.Roots);
        Assert.Equal(["**/generated/**"], config.Excludes);
        var rule = Assert.Single(config.CustomRules);
        Assert.Equal(ArtifactCategory.Build, rule.Category);
        Assert.False(rule.Preselected);
    }

    [Fact]
    public void Load_absent_implicit_file_returns_version_one_empty_defaults()
    {
        var result = ConfigLoader.LoadResolvedPath(Path.Combine(directory, "missing.json"), isExplicit: false);

        Assert.True(result.IsSuccess, result.Error);
        var config = Assert.IsType<DevCleanerConfig>(result.Config);
        Assert.Equal(1, config.SchemaVersion);
        Assert.Empty(config.Roots);
        Assert.Empty(config.Excludes);
        Assert.Empty(config.DisabledRules);
        Assert.Empty(config.CustomRules);
    }

    [Fact]
    public void Load_rejects_an_explicit_missing_file()
    {
        var path = Path.Combine(directory, "missing.json");

        var result = ConfigLoader.Load(path);

        Assert.False(result.IsSuccess);
        Assert.Contains("does not exist", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Load_rejects_an_explicit_directory()
    {
        Directory.CreateDirectory(directory);

        var result = ConfigLoader.Load(directory);

        Assert.False(result.IsSuccess);
        Assert.Contains("directory", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Load_reports_an_explicit_access_failure_where_unix_permissions_are_enforced()
    {
        if (OperatingSystem.IsWindows()) return;

        var path = Write("{\"schemaVersion\":1}");
        var originalMode = File.GetUnixFileMode(path);
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.None);

            try
            {
                using var probe = File.OpenRead(path);
                return;
            }
            catch (UnauthorizedAccessException)
            {
                // The access-failure assertion applies only where Unix mode bits are enforced.
            }

            var result = ConfigLoader.Load(path);

            Assert.False(result.IsSuccess);
            Assert.Contains("read", result.Error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.SetUnixFileMode(path, originalMode);
        }
    }

    [Fact]
    public void GetDefaultPath_uses_the_platform_configuration_location()
    {
        var path = ConfigLoader.GetDefaultPath();

        Assert.Equal("config.json", Path.GetFileName(path));
        Assert.Equal("repoglean", Path.GetFileName(Path.GetDirectoryName(path)!));
        if (OperatingSystem.IsWindows())
        {
            Assert.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), path, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            Assert.StartsWith(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"), path, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("{\"schemaVersion\":2}", "schemaVersion")]
    [InlineData("{\"schemaVersion\":1,\"customRules\":[{\"id\":\"x\",\"patterns\":[\"**/x\"]}]}", "category")]
    [InlineData("{\"schemaVersion\":1,\"customRules\":[{\"id\":\"bad id\",\"category\":\"Build\",\"patterns\":[\"**/x\"]}]}", "id")]
    [InlineData("{\"schemaVersion\":1,\"customRules\":[{\"id\":\"x\",\"category\":\"Logs\",\"patterns\":[\"**/x\"]}]}", "category")]
    [InlineData("{\"schemaVersion\":1,\"customRules\":[{\"id\":\"x\",\"category\":0,\"patterns\":[\"**/x\"]}]}", "category")]
    [InlineData("{\"schemaVersion\":1,\"customRules\":[{\"id\":\"x\",\"category\":\"Build\",\"patterns\":[]}]}", "pattern")]
    [InlineData("{\"schemaVersion\":1,\"customRules\":[{\"id\":\"x\",\"category\":\"Build\",\"patterns\":[\"\"]}]}", "pattern")]
    public void Load_rejects_invalid_configuration(string json, string errorPart)
    {
        var result = ConfigLoader.Load(Write(json));

        Assert.False(result.IsSuccess);
        Assert.Contains(errorPart, result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Load_rejects_custom_preselected_true_instead_of_silently_discarding_it()
    {
        var result = ConfigLoader.Load(Write("""
            {"schemaVersion":1,"customRules":[{
              "id":"company.generated","category":"Build","patterns":["**/.generated"],"preselected":true
            }]}
            """));

        Assert.False(result.IsSuccess);
        Assert.Contains("preselected", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("/tmp/**")]
    [InlineData("C:/temp/**")]
    [InlineData("../../bin/**")]
    [InlineData("./bin/**")]
    [InlineData("src/./bin/**")]
    [InlineData("src/../bin/**")]
    public void Load_rejects_custom_patterns_that_are_not_repository_relative(string pattern)
    {
        var result = ConfigLoader.Load(Write($$"""
            { "schemaVersion": 1, "customRules": [
              { "id": "company.build", "category": "Build", "patterns": ["{{pattern}}"] }
            ] }
            """));

        Assert.False(result.IsSuccess);
        Assert.Contains("repository-relative", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Load_rejects_invalid_json_and_duplicate_custom_rule_ids()
    {
        var invalidJson = ConfigLoader.Load(Write("{ not json }"));
        var duplicate = ConfigLoader.Load(Write("""
            { "schemaVersion": 1, "customRules": [
              { "id": "company.cache", "category": "Cache", "patterns": ["**/.cache/**"] },
              { "id": "company.cache", "category": "Cache", "patterns": ["**/cache/**"] }
            ] }
            """));

        Assert.False(invalidJson.IsSuccess);
        Assert.Contains("JSON", invalidJson.Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(duplicate.IsSuccess);
        Assert.Contains("duplicate", duplicate.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Load_case_variant_recognized_properties_use_the_effective_last_value()
    {
        var result = ConfigLoader.Load(Write("""
            { "SCHEMAVERSION": 1, "schemaVersion": 1,
              "ROOTS": ["first-root"], "roots": ["effective-root"],
              "CUSTOMRULES": [{ "id": "company.first", "category": "Build", "patterns": ["**/first/**"] }],
              "customRules": [{
                "ID": "company.shadowed", "id": "company.effective",
                "CATEGORY": "Build", "category": "Cache",
                "PATTERNS": ["**/shadowed/**"], "patterns": ["**/effective/**"]
              }]
            }
            """));

        Assert.True(result.IsSuccess, result.Error);
        var config = Assert.IsType<DevCleanerConfig>(result.Config);
        Assert.Equal(["effective-root"], config.Roots);
        var rule = Assert.Single(config.CustomRules);
        Assert.Equal("company.effective", rule.Id);
        Assert.Equal(ArtifactCategory.Cache, rule.Category);
        Assert.Equal(["**/effective/**"], rule.Patterns);
    }

    [Fact]
    public void Load_rejects_an_invalid_shadowed_custom_rule_member_occurrence()
    {
        var result = ConfigLoader.Load(Write("""
            { "schemaVersion": 1,
              "customRules": [{
                "ID": "bad id",
                "id": "company.generated",
                "category": "Build",
                "patterns": ["**/generated/**"]
              }]
            }
            """));

        Assert.False(result.IsSuccess);
        Assert.Contains("id", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Load_rejects_missing_category_in_the_effective_last_case_variant_custom_rules()
    {
        var result = ConfigLoader.Load(Write("""
            { "schemaVersion": 1,
              "CUSTOMRULES": [{ "id": "company.first", "category": "Build", "patterns": ["**/first/**"] }],
              "customRules": [{ "id": "company.effective", "patterns": ["**/effective/**"] }]
            }
            """));

        Assert.False(result.IsSuccess);
        Assert.Contains("category", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(ConfigurationContractSamples.All), MemberType = typeof(ConfigurationContractSamples))]
    public void Load_matches_the_shared_configuration_contract_samples(string name, string json, bool expectedValid)
    {
        var result = ConfigLoader.Load(Write(json));

        Assert.True(result.IsSuccess == expectedValid, $"Loader result mismatch for '{name}': {result.Error}");
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    private string Write(string content)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{Guid.NewGuid():N}.json");
        File.WriteAllText(path, content);
        return path;
    }
}
