using RepoGlean.Rules;

namespace RepoGlean.Configuration;

public sealed record RepoGleanConfig
{
    public static RepoGleanConfig Default { get; } = new();

    public int SchemaVersion { get; init; } = 1;

    public IReadOnlyList<string> Roots { get; init; } = [];

    public IReadOnlyList<string> Excludes { get; init; } = [];

    public IReadOnlyList<string> DisabledRules { get; init; } = [];

    public IReadOnlyList<ArtifactRule> CustomRules { get; init; } = [];
}
