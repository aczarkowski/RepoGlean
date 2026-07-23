using System.Security;
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
        var isExplicit = path is not null;
        string resolvedPath;
        try
        {
            resolvedPath = Path.GetFullPath(isExplicit ? path! : GetDefaultPath());
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return ConfigLoadResult.Failure($"Invalid configuration path: {exception.Message}");
        }

        return LoadResolvedPath(resolvedPath, isExplicit);
    }

    internal static ConfigLoadResult LoadResolvedPath(string resolvedPath, bool isExplicit)
    {
        if (Directory.Exists(resolvedPath))
        {
            return ConfigLoadResult.Failure($"Configuration path '{resolvedPath}' is a directory, not a file.");
        }

        DevCleanerConfig? config;
        try
        {
            var json = File.ReadAllText(resolvedPath);
            if (!ValidateRecognizedOccurrences(json, out var propertyError)) return ConfigLoadResult.Failure(propertyError);
            config = JsonSerializer.Deserialize(json, JsonContext.DevCleanerConfig);
        }
        catch (JsonException exception)
        {
            return ConfigLoadResult.Failure($"Invalid JSON configuration: {exception.Message}");
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return isExplicit
                ? ConfigLoadResult.Failure($"Configuration file '{resolvedPath}' does not exist.")
                : ConfigLoadResult.Success(DevCleanerConfig.Default);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or SecurityException)
        {
            return ConfigLoadResult.Failure($"Unable to read configuration: {exception.Message}");
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

    private static bool ValidateRecognizedOccurrences(string json, out string error)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        });

        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            error = "The configuration root must be an object.";
            return false;
        }

        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (NameEquals(property, "schemaVersion") && !IsSchemaVersion(property.Value))
            {
                error = "Every schemaVersion occurrence must be 1.";
                return false;
            }

            if (NameEquals(property, "roots") && !IsNullableStringArray(property.Value))
            {
                error = "Every roots occurrence must be null or an array of strings.";
                return false;
            }

            if (NameEquals(property, "excludes") && !IsNullableStringArray(property.Value))
            {
                error = "Every excludes occurrence must be null or an array of strings.";
                return false;
            }

            if (NameEquals(property, "disabledRules") && !IsNullableStringArray(property.Value))
            {
                error = "Every disabledRules occurrence must be null or an array of strings.";
                return false;
            }

            if (NameEquals(property, "customRules") && !ValidateCustomRulesOccurrence(property.Value, out error))
            {
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    private static bool ValidateCustomRulesOccurrence(JsonElement value, out string error)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            error = string.Empty;
            return true;
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            error = "Every customRules occurrence must be null or an array of rule objects.";
            return false;
        }

        foreach (var rule in value.EnumerateArray())
        {
            if (!ValidateCustomRuleOccurrence(rule, out error))
            {
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    private static bool ValidateCustomRuleOccurrence(JsonElement rule, out string error)
    {
        if (rule.ValueKind != JsonValueKind.Object)
        {
            error = "Every custom rule must be an object.";
            return false;
        }

        var hasId = false;
        var hasCategory = false;
        var hasPatterns = false;

        foreach (var property in rule.EnumerateObject())
        {
            if (NameEquals(property, "id"))
            {
                hasId = true;
                if (property.Value.ValueKind != JsonValueKind.String || !ArtifactRule.IsValidId(property.Value.GetString()))
                {
                    error = "Every custom rule id occurrence must be a valid id.";
                    return false;
                }
            }
            else if (NameEquals(property, "category"))
            {
                hasCategory = true;
                if (property.Value.ValueKind != JsonValueKind.String ||
                    !Enum.GetNames<ArtifactCategory>().Contains(property.Value.GetString(), StringComparer.OrdinalIgnoreCase))
                {
                    error = "Every custom rule category occurrence must name a standard category.";
                    return false;
                }
            }
            else if (NameEquals(property, "patterns"))
            {
                hasPatterns = true;
                if (!IsValidPatterns(property.Value))
                {
                    error = "Every custom rule patterns occurrence must contain one or more safe, non-empty repository-relative patterns.";
                    return false;
                }
            }
            else if (NameEquals(property, "markers") && !IsValidMarkers(property.Value))
            {
                error = "Every custom rule markers occurrence must be null or an array of non-empty patterns.";
                return false;
            }
            else if (NameEquals(property, "preselected") && property.Value.ValueKind != JsonValueKind.False)
            {
                error = "Every custom rule preselected occurrence must be false in schema version 1.";
                return false;
            }
        }

        if (!hasId)
        {
            error = "Each custom rule requires an id.";
            return false;
        }

        if (!hasCategory)
        {
            error = "Each custom rule requires a category.";
            return false;
        }

        if (!hasPatterns)
        {
            error = "Each custom rule requires patterns.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool IsSchemaVersion(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var version) && version == 1;

    private static bool IsNullableStringArray(JsonElement value) =>
        value.ValueKind == JsonValueKind.Null ||
        value.ValueKind == JsonValueKind.Array && value.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String);

    private static bool IsValidPatterns(JsonElement value) =>
        value.ValueKind == JsonValueKind.Array &&
        value.GetArrayLength() > 0 &&
        value.EnumerateArray().All(item =>
            item.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(item.GetString()) &&
            IsRepositoryRelativePattern(item.GetString()!));

    private static bool IsValidMarkers(JsonElement value) =>
        value.ValueKind == JsonValueKind.Null ||
        value.ValueKind == JsonValueKind.Array && value.EnumerateArray().All(item =>
            item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()));

    private static bool NameEquals(JsonProperty property, string name) =>
        string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase);

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
