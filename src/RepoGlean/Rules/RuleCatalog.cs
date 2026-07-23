using RepoGlean.Configuration;

namespace RepoGlean.Rules;

public sealed record RuleCatalog(IReadOnlyList<ArtifactRule> Rules)
{
    public static RuleCatalog Create(RepoGleanConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var disabled = new HashSet<string>(config.DisabledRules, StringComparer.OrdinalIgnoreCase);
        var rules = BuiltInRules.All.Where(rule => !disabled.Contains(rule.Id)).ToList();
        var ids = new HashSet<string>(BuiltInRules.Ids, StringComparer.OrdinalIgnoreCase);

        foreach (var customRule in config.CustomRules)
        {
            if (!ids.Add(customRule.Id))
            {
                throw new ArgumentException($"Custom rule id '{customRule.Id}' collides with a built-in or custom rule.", nameof(config));
            }

            rules.Add(customRule with { Preselected = false });
        }

        return new RuleCatalog(rules.AsReadOnly());
    }
}
