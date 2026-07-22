namespace DevCleaner.Cli;

public enum CommandKind
{
    Scan,
    Clean,
    RulesList,
    ConfigPath,
    ConfigShow,
    ConfigValidate,
    Help,
    Version,
}

public enum OutputFormat
{
    Table,
    Json,
}

public enum ArtifactCategory
{
    Build,
    Cache,
    Test,
    Ide,
    Dependency,
}

public sealed record CliOptions(
    CommandKind Command,
    IReadOnlyList<string> Roots,
    IReadOnlyList<string> Repositories,
    IReadOnlyList<ArtifactCategory> Categories,
    IReadOnlyList<string> Exclusions,
    long? MinimumBytes,
    bool AllDrives,
    bool Details,
    bool DryRun,
    bool Yes,
    bool All,
    OutputFormat OutputFormat,
    bool NoColor,
    string? ConfigPath,
    bool Help,
    bool Version);

public sealed record ParseResult<T>(T? Value, string? Error)
    where T : class
{
    public bool IsSuccess => Value is not null && Error is null;

    public static ParseResult<T> Success(T value) => new(value, null);

    public static ParseResult<T> Failure(string error) => new(null, error);
}
