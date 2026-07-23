using System.Text.Json;
using System.Text.Json.Serialization;
using RepoGlean.Rules;

namespace RepoGlean.Configuration;

[JsonSourceGenerationOptions(AllowTrailingCommas = true, PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, ReadCommentHandling = JsonCommentHandling.Skip, WriteIndented = true)]
[JsonSerializable(typeof(RepoGleanConfig))]
[JsonSerializable(typeof(ArtifactRule))]
internal partial class RepoGleanJsonContext : JsonSerializerContext;
