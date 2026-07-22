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

public sealed record CliOptions
{
    public CliOptions(
        CommandKind command,
        IReadOnlyList<string> roots,
        IReadOnlyList<string> repositories,
        IReadOnlyList<ArtifactCategory> categories,
        IReadOnlyList<string> exclusions,
        long? minimumBytes,
        bool allDrives,
        bool details,
        bool dryRun,
        bool yes,
        bool all,
        OutputFormat outputFormat,
        bool noColor,
        string? configPath,
        bool help,
        bool version,
        bool quiet = false,
        bool verbose = false,
        bool noProgress = false)
    {
        Command = command;
        Roots = Freeze(roots);
        Repositories = Freeze(repositories);
        Categories = Freeze(categories);
        Exclusions = Freeze(exclusions);
        MinimumBytes = minimumBytes;
        AllDrives = allDrives;
        Details = details;
        DryRun = dryRun;
        Yes = yes;
        All = all;
        OutputFormat = outputFormat;
        NoColor = noColor;
        ConfigPath = configPath;
        Help = help;
        Version = version;
        Quiet = quiet;
        Verbose = verbose;
        NoProgress = noProgress;
    }

    public CommandKind Command { get; }

    public IReadOnlyList<string> Roots { get; }

    public IReadOnlyList<string> Repositories { get; }

    public IReadOnlyList<ArtifactCategory> Categories { get; }

    public IReadOnlyList<string> Exclusions { get; }

    public long? MinimumBytes { get; }

    public bool AllDrives { get; }

    public bool Details { get; }

    public bool DryRun { get; }

    public bool Yes { get; }

    public bool All { get; }

    public OutputFormat OutputFormat { get; }

    public bool NoColor { get; }

    public string? ConfigPath { get; }

    public bool Help { get; }

    public bool Version { get; }

    public bool Quiet { get; }

    public bool Verbose { get; }

    public bool NoProgress { get; }

    private static IReadOnlyList<T> Freeze<T>(IReadOnlyList<T> values) => Array.AsReadOnly(values.ToArray());
}

public sealed record ParseResult<T>(T? Value, string? Error)
    where T : class
{
    public bool IsSuccess => Value is not null && Error is null;

    public static ParseResult<T> Success(T value) => new(value, null);

    public static ParseResult<T> Failure(string error) => new(null, error);
}
