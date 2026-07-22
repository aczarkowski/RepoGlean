using DevCleaner.Cli;
using DevCleaner.Configuration;
using DevCleaner.Rules;

namespace DevCleaner.Tests.Rules;

public sealed class RuleCatalogTests
{
    [Fact]
    public void Create_contains_the_supported_ecosystem_rules()
    {
        var catalog = RuleCatalog.Create(DevCleanerConfig.Default);

        Assert.Contains(catalog.Rules, rule => rule.Id == "dotnet.bin");
        Assert.Contains(catalog.Rules, rule => rule.Id == "node.node-modules" && rule.Category == ArtifactCategory.Dependency && !rule.Preselected);
        Assert.Contains(catalog.Rules, rule => rule.Id == "jvm.maven-target");
        Assert.Contains(catalog.Rules, rule => rule.Id == "rust.target");
        Assert.Contains(catalog.Rules, rule => rule.Id == "python.pycache");
        Assert.Contains(catalog.Rules, rule => rule.Id == "go.bin");
        Assert.Contains(catalog.Rules, rule => rule.Id == "cpp.cmake-build");
        Assert.Contains(catalog.Rules, rule => rule.Id == "apple.derived-data");
    }

    [Fact]
    public void Create_disables_built_ins_and_adds_unselected_custom_rules()
    {
        var config = DevCleanerConfig.Default with
        {
            DisabledRules = ["dotnet.bin"],
            CustomRules = [new ArtifactRule("company.generated", ArtifactCategory.Build, ["**/generated/**"], ["**/*.csproj"], true)],
        };

        var catalog = RuleCatalog.Create(config);

        Assert.DoesNotContain(catalog.Rules, rule => rule.Id == "dotnet.bin");
        var customRule = Assert.Single(catalog.Rules, rule => rule.Id == "company.generated");
        Assert.False(customRule.Preselected);
        Assert.Equal(["**/*.csproj"], customRule.Markers);
    }

    [Fact]
    public void Create_rejects_custom_rule_collisions_with_built_ins()
    {
        var config = DevCleanerConfig.Default with
        {
            CustomRules = [new ArtifactRule("dotnet.bin", ArtifactCategory.Build, ["**/other/**"], [], false)],
        };

        var exception = Assert.Throws<ArgumentException>(() => RuleCatalog.Create(config));

        Assert.Contains("built-in", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rule_requires_any_marker_before_it_is_active()
    {
        var rule = new ArtifactRule("company.build", ArtifactCategory.Build, ["**/build/**"], ["**/*.csproj", "**/package.json"], false);

        Assert.True(rule.IsActiveFor(["src/app.csproj"]));
        Assert.False(rule.IsActiveFor(["README.md"]));
    }
}
