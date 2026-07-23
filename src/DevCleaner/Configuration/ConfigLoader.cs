using System.Text.Json;
using System.Text.Json.Serialization;
using DevCleaner.Cli;
using DevCleaner.Rules;

namespace DevCleaner.Configuration;

public sealed record ConfigLoadResult(DevCleanerConfig? Config, string? Error)
{
    public bool IsSuccess => Config is not null && Error is null;

    public static ConfigLoadResult Success(DevCleanerConfig config) => new(config, null);

    public static ConfigLoadResult Failure(string error) => new(null, error);
}

public static class ConfigLoader
{
    private static readonly DevCleanerJsonContext JsonContext = new(CreateSerializerOptions());

    public static string GetDefaultPath()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "devcleaner", "config.json");
        }

        var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(configHome))
        {
            configHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        }

        return Path.Combine(configHome, "devcleaner", "config.json");
    }

    public static ConfigLoadResult Load(string? path)
    {
        var resolvedPath = string.IsNullOrWhiteSpace(path) ? GetDefaultPath() : path;
        if (!File.Exists(resolvedPath)) return ConfigLoadResult.Success(DevCleanerConfig.Default);

        DevCleanerConfig? config;
        try
        {
            var json = File.ReadAllText(resolvedPath);
            if (!HasRequiredCustomRuleProperties(json, out var propertyError)) return ConfigLoadResult.Failure(propertyError);
            config = JsonSerializer.Deserialize(json, JsonContext.DevCleanerConfig);
        }
        catch (JsonException exception)
        {
            return ConfigLoadResult.Failure($"Invalid JSON configuration: {exception.Message}");
        }
        catch (IOException exception)
        {
            return ConfigLoadResult.Failure($"Unable to read configuration: {exception.Message}");
        }

        if (config is null) return ConfigLoadResult.Failure("Invalid JSON configuration: the document is empty.");
        config = Normalize(config);
        return Validate(config, out var error) ? ConfigLoadResult.Success(config) : ConfigLoadResult.Failure(error);
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions
        {
            AllowTrailingCommas = true,
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
        };
        options.Converters.Add(new JsonStringEnumConverter<ArtifactCategory>(namingPolicy: null, allowIntegerValues: false));
        return options;
    }

    private static DevCleanerConfig Normalize(DevCleanerConfig config) => config with
    {
        Roots = config.Roots ?? [],
        Excludes = config.Excludes ?? [],
        DisabledRules = config.DisabledRules ?? [],
        CustomRules = (config.CustomRules ?? []).Select(rule => rule is null
            ? new ArtifactRule(string.Empty, default, [], [], false)
            : rule with
        {
            Patterns = rule.Patterns ?? [],
            Markers = rule.Markers ?? [],
        }).ToArray(),
    };

    private static bool Validate(DevCleanerConfig config, out string error)
    {
        if (config.SchemaVersion != 1)
        {
            error = $"Unsupported schemaVersion '{config.SchemaVersion}'; expected 1.";
            return false;
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in config.CustomRules)
        {
            if (rule is null || !ArtifactRule.IsValidId(rule.Id))
            {
                error = "Each custom rule requires a valid id.";
                return false;
            }

            if (!Enum.IsDefined(rule.Category))
            {
                error = $"Custom rule '{rule.Id}' has an invalid category.";
                return false;
            }

            if (rule.Patterns is null || rule.Patterns.Count == 0 || rule.Patterns.Any(string.IsNullOrWhiteSpace))
            {
                error = $"Custom rule '{rule.Id}' requires one or more non-empty patterns.";
                return false;
            }

            if (rule.Patterns.Any(pattern => !IsRepositoryRelativePattern(pattern)))
            {
                error = $"Custom rule '{rule.Id}' patterns must be repository-relative and cannot contain '.' or '..' segments.";
                return false;
            }

            if (rule.Markers is null || rule.Markers.Any(string.IsNullOrWhiteSpace))
            {
                error = $"Custom rule '{rule.Id}' has an empty marker pattern.";
                return false;
            }

            if (rule.Preselected)
            {
                error = $"Custom rule '{rule.Id}' cannot set preselected to true in schema version 1.";
                return false;
            }

            if (!ids.Add(rule.Id))
            {
                error = $"Duplicate custom rule id '{rule.Id}'.";
                return false;
            }

            if (BuiltInRules.Ids.Contains(rule.Id, StringComparer.OrdinalIgnoreCase))
            {
                error = $"Custom rule id '{rule.Id}' collides with a built-in rule.";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    private static bool HasRequiredCustomRuleProperties(string json, out string error)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        });

        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            error = string.Empty;
            return true;
        }

        if (TryGetLastProperty(document.RootElement, "customRules", out var customRules) && customRules.ValueKind == JsonValueKind.Array)
        {
            foreach (var rule in customRules.EnumerateArray())
            {
                if (rule.ValueKind == JsonValueKind.Object && !TryGetLastProperty(rule, "category", out _))
                {
                    error = "Each custom rule requires a category.";
                    return false;
                }
            }
        }

        error = string.Empty;
        return true;
    }

    private static bool TryGetLastProperty(JsonElement element, string name, out JsonElement value)
    {
        var found = false;
        value = default;
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                found = true;
            }
        }

        return found;
    }

    private static bool IsRepositoryRelativePattern(string pattern)
    {
        var normalized = pattern.Replace('\\', '/');
        if (normalized.StartsWith("/", StringComparison.Ordinal) ||
            (normalized.Length >= 2 && char.IsAsciiLetter(normalized[0]) && normalized[1] == ':'))
        {
            return false;
        }

        return !normalized.Split('/').Any(segment => segment is "." or "..");
    }
}
