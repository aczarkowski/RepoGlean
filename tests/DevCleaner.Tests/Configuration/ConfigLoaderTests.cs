using DevCleaner.Cli;
using DevCleaner.Configuration;

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
    public void Load_missing_file_returns_version_one_empty_defaults()
    {
        var result = ConfigLoader.Load(Path.Combine(directory, "missing.json"));

        Assert.True(result.IsSuccess, result.Error);
        var config = Assert.IsType<DevCleanerConfig>(result.Config);
        Assert.Equal(1, config.SchemaVersion);
        Assert.Empty(config.Roots);
        Assert.Empty(config.Excludes);
        Assert.Empty(config.DisabledRules);
        Assert.Empty(config.CustomRules);
    }

    [Fact]
    public void GetDefaultPath_uses_the_platform_configuration_location()
    {
        var path = ConfigLoader.GetDefaultPath();

        Assert.Equal("config.json", Path.GetFileName(path));
        Assert.Equal("devcleaner", Path.GetFileName(Path.GetDirectoryName(path)!));
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
