using DevCleaner.Rules;

namespace DevCleaner.Configuration;

public sealed record DevCleanerConfig
{
    public static DevCleanerConfig Default { get; } = new();

    public int SchemaVersion { get; init; } = 1;

    public IReadOnlyList<string> Roots { get; init; } = [];

    public IReadOnlyList<string> Excludes { get; init; } = [];

    public IReadOnlyList<string> DisabledRules { get; init; } = [];

    public IReadOnlyList<ArtifactRule> CustomRules { get; init; } = [];
}
