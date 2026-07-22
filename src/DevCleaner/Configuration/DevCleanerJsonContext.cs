using System.Text.Json;
using System.Text.Json.Serialization;
using DevCleaner.Rules;

namespace DevCleaner.Configuration;

[JsonSourceGenerationOptions(AllowTrailingCommas = true, PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, ReadCommentHandling = JsonCommentHandling.Skip, WriteIndented = true)]
[JsonSerializable(typeof(DevCleanerConfig))]
[JsonSerializable(typeof(ArtifactRule))]
internal partial class DevCleanerJsonContext : JsonSerializerContext;
