using System.Text.Json.Serialization;

namespace DevCleaner.Output;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
[JsonSerializable(typeof(ReportDocument))]
[JsonSerializable(typeof(ReportMessage))]
[JsonSerializable(typeof(ReportTotals))]
[JsonSerializable(typeof(RepositoryReport))]
[JsonSerializable(typeof(CandidateReport))]
[JsonSerializable(typeof(RuleReport))]
internal partial class ReportJsonContext : JsonSerializerContext;
