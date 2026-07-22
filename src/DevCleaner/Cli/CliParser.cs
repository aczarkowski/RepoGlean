namespace DevCleaner.Cli;

public static class CliParser
{
    public static ParseResult<CliOptions> Parse(string[] arguments)
    {
        var roots = new List<string>();
        var repositories = new List<string>();
        var categories = new List<ArtifactCategory>();
        var exclusions = new List<string>();
        CommandKind? command = null;
        var awaitingSubcommand = false;
        long? minimumBytes = null;
        var allDrives = false;
        var details = false;
        var dryRun = false;
        var yes = false;
        var all = false;
        var outputFormat = OutputFormat.Table;
        var noColor = false;
        string? configPath = null;
        var help = false;
        var version = false;

        for (var index = 0; index < arguments.Length; index++)
        {
            var argument = arguments[index];
            if (argument.StartsWith("--", StringComparison.Ordinal))
            {
                switch (argument)
                {
                    case "--repo":
                        if (!TryReadValue(arguments, ref index, argument, out var repository, out var error)) return ParseResult<CliOptions>.Failure(error);
                        repositories.Add(repository);
                        break;
                    case "--category":
                        if (!TryReadValue(arguments, ref index, argument, out var categoryValue, out error)) return ParseResult<CliOptions>.Failure(error);
                        if (!TryParseCategory(categoryValue, out var category)) return ParseResult<CliOptions>.Failure($"Invalid category '{categoryValue}'.");
                        categories.Add(category);
                        break;
                    case "--exclude":
                        if (!TryReadValue(arguments, ref index, argument, out var exclusion, out error)) return ParseResult<CliOptions>.Failure(error);
                        exclusions.Add(exclusion);
                        break;
                    case "--min-size":
                        if (!TryReadValue(arguments, ref index, argument, out var minimumSize, out error)) return ParseResult<CliOptions>.Failure(error);
                        if (!ByteSizeParser.TryParse(minimumSize, out var parsedMinimumBytes)) return ParseResult<CliOptions>.Failure($"Invalid byte size '{minimumSize}'.");
                        minimumBytes = parsedMinimumBytes;
                        break;
                    case "--format":
                        if (!TryReadValue(arguments, ref index, argument, out var formatValue, out error)) return ParseResult<CliOptions>.Failure(error);
                        if (!TryParseFormat(formatValue, out outputFormat)) return ParseResult<CliOptions>.Failure($"Invalid output format '{formatValue}'.");
                        break;
                    case "--config":
                        if (!TryReadValue(arguments, ref index, argument, out var path, out error)) return ParseResult<CliOptions>.Failure(error);
                        configPath = path;
                        break;
                    case "--all-drives": allDrives = true; break;
                    case "--details": details = true; break;
                    case "--dry-run": dryRun = true; break;
                    case "--yes": yes = true; break;
                    case "--all": all = true; break;
                    case "--no-color": noColor = true; break;
                    case "--help": help = true; break;
                    case "--version": version = true; break;
                    default: return ParseResult<CliOptions>.Failure($"Unknown option '{argument}'.");
                }

                continue;
            }

            if (command is null)
            {
                if (!TryParseCommand(argument, out var parsedCommand, out awaitingSubcommand))
                {
                    return ParseResult<CliOptions>.Failure($"Unknown command '{argument}'.");
                }

                command = parsedCommand;
                continue;
            }

            if (awaitingSubcommand)
            {
                if (!TryParseSubcommand(command.Value, argument, out var subcommand))
                {
                    return ParseResult<CliOptions>.Failure($"Unknown subcommand '{argument}'.");
                }

                command = subcommand;
                awaitingSubcommand = false;
                continue;
            }

            if (command is CommandKind.Scan or CommandKind.Clean)
            {
                roots.Add(argument);
                continue;
            }

            return ParseResult<CliOptions>.Failure($"Unexpected argument '{argument}'.");
        }

        if (help)
        {
            command = CommandKind.Help;
        }
        else if (version)
        {
            command = CommandKind.Version;
        }

        if (command is null || awaitingSubcommand)
        {
            return ParseResult<CliOptions>.Failure("A command and required subcommand are required.");
        }

        if (yes && command != CommandKind.Clean)
        {
            return ParseResult<CliOptions>.Failure("--yes is only valid with clean.");
        }

        if (command == CommandKind.Clean && yes && !all && repositories.Count == 0 && categories.Count == 0)
        {
            return ParseResult<CliOptions>.Failure("clean --yes requires --all, --repo, or --category.");
        }

        if (command == CommandKind.Clean && outputFormat == OutputFormat.Json && !yes && !dryRun)
        {
            return ParseResult<CliOptions>.Failure("--format json with clean requires --yes or --dry-run.");
        }

        return ParseResult<CliOptions>.Success(new CliOptions(
            command.Value,
            Array.AsReadOnly(roots.ToArray()),
            Array.AsReadOnly(repositories.ToArray()),
            Array.AsReadOnly(categories.ToArray()),
            Array.AsReadOnly(exclusions.ToArray()),
            minimumBytes,
            allDrives,
            details,
            dryRun,
            yes,
            all,
            outputFormat,
            noColor,
            configPath,
            help,
            version));
    }

    private static bool TryReadValue(string[] arguments, ref int index, string option, out string value, out string error)
    {
        if (index + 1 >= arguments.Length || arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            value = string.Empty;
            error = $"Option '{option}' requires a value.";
            return false;
        }

        value = arguments[++index];
        error = string.Empty;
        return true;
    }

    private static bool TryParseCommand(string value, out CommandKind command, out bool awaitingSubcommand)
    {
        awaitingSubcommand = false;
        switch (value.ToLowerInvariant())
        {
            case "scan": command = CommandKind.Scan; return true;
            case "clean": command = CommandKind.Clean; return true;
            case "rules": command = CommandKind.RulesList; awaitingSubcommand = true; return true;
            case "config": command = CommandKind.ConfigPath; awaitingSubcommand = true; return true;
            case "help": command = CommandKind.Help; return true;
            case "version": command = CommandKind.Version; return true;
            default: command = default; return false;
        }
    }

    private static bool TryParseSubcommand(CommandKind parent, string value, out CommandKind command)
    {
        command = default;
        if (parent == CommandKind.RulesList && string.Equals(value, "list", StringComparison.OrdinalIgnoreCase))
        {
            command = CommandKind.RulesList;
            return true;
        }

        if (parent != CommandKind.ConfigPath)
        {
            return false;
        }

        switch (value.ToLowerInvariant())
        {
            case "path": command = CommandKind.ConfigPath; return true;
            case "show": command = CommandKind.ConfigShow; return true;
            case "validate": command = CommandKind.ConfigValidate; return true;
            default: return false;
        }
    }

    private static bool TryParseCategory(string value, out ArtifactCategory category) =>
        Enum.TryParse(value, ignoreCase: true, out category) && Enum.IsDefined(category);

    private static bool TryParseFormat(string value, out OutputFormat format) =>
        Enum.TryParse(value, ignoreCase: true, out format) && Enum.IsDefined(format);
}
