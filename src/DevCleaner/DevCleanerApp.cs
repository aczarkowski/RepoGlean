using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using DevCleaner.Cli;
using DevCleaner.Cleaning;
using DevCleaner.Configuration;
using DevCleaner.Git;
using DevCleaner.Output;
using DevCleaner.Rules;
using DevCleaner.Scanning;

namespace DevCleaner;

internal sealed record AppRuntime(
    string GitExecutable,
    string HomeDirectory,
    bool IsErrorInteractive,
    bool IsOutputInteractive = false,
    IDriveRootProvider? DriveRootProvider = null)
{
    public static AppRuntime Create(TextWriter stdout, TextWriter stderr) => Create(
        stdout,
        stderr,
        Console.IsOutputRedirected,
        Console.IsErrorRedirected);

    internal static AppRuntime Create(
        TextWriter stdout,
        TextWriter stderr,
        bool isOutputRedirected,
        bool isErrorRedirected) => new(
        "git",
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ReferenceEquals(stderr, Console.Error) && !isErrorRedirected,
        ReferenceEquals(stdout, Console.Out) && !isOutputRedirected);
}

public static class DevCleanerApp
{
    public static Task<int> RunAsync(
        string[] arguments,
        TextReader input,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken = default) =>
        RunAsync(arguments, input, stdout, stderr, AppRuntime.Create(stdout, stderr), cancellationToken);

    internal static async Task<int> RunAsync(
        string[] arguments,
        TextReader input,
        TextWriter stdout,
        TextWriter stderr,
        AppRuntime runtime,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        ArgumentNullException.ThrowIfNull(runtime);

        if (arguments.Length == 0)
        {
            WriteHelp(stdout);
            return 0;
        }

        var parseResult = CliParser.Parse(arguments);
        if (!parseResult.IsSuccess)
        {
            await stderr.WriteLineAsync($"Error: {parseResult.Error}").ConfigureAwait(false);
            WriteHelp(stderr);
            return 2;
        }

        var options = parseResult.Value!;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (options.Command)
            {
                case CommandKind.Help:
                    WriteHelp(stdout);
                    return 0;
                case CommandKind.Version:
                    stdout.WriteLine($"devcleaner {GetVersion()}");
                    return 0;
                case CommandKind.ConfigPath:
                    stdout.WriteLine(ResolveConfigPath(options.ConfigPath));
                    return 0;
            }

            var loadResult = ConfigLoader.Load(options.ConfigPath);
            if (!loadResult.IsSuccess)
            {
                await stderr.WriteLineAsync($"Configuration error: {loadResult.Error}").ConfigureAwait(false);
                return 2;
            }

            var config = loadResult.Config!;
            var configPath = ResolveConfigPath(options.ConfigPath);
            switch (options.Command)
            {
                case CommandKind.ConfigShow:
                    await WriteConfigAsync(config, stdout, cancellationToken).ConfigureAwait(false);
                    return 0;
                case CommandKind.ConfigValidate:
                    stdout.WriteLine($"Configuration is valid: {configPath}");
                    return 0;
                case CommandKind.RulesList:
                    var rulesReport = ReportDocument.FromRules(config);
                    if (options.OutputFormat == OutputFormat.Json)
                    {
                        await JsonReportWriter.WriteAsync(rulesReport, stdout, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        HumanReportWriter.WriteRules(rulesReport, stdout);
                    }

                    return 0;
                case CommandKind.Scan:
                    return await RunScanAsync(options, config, runtime, stdout, stderr, cancellationToken).ConfigureAwait(false);
                case CommandKind.Clean:
                    return await RunCleanAsync(options, config, runtime, input, stdout, stderr, cancellationToken).ConfigureAwait(false);
                default:
                    await stderr.WriteLineAsync("Error: unsupported command.").ConfigureAwait(false);
                    return 2;
            }
        }
        catch (OperationCanceledException)
        {
            if (options.OutputFormat == OutputFormat.Json)
            {
                await JsonReportWriter.WriteAsync(ReportDocument.Interrupted(OperationName(options.Command)), stdout, CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                await stderr.WriteLineAsync("Operation interrupted.").ConfigureAwait(false);
            }

            return 130;
        }
        catch (Exception exception) when (exception is GitUnavailableException or GitCommandException or IOException or UnauthorizedAccessException)
        {
            if (options.OutputFormat == OutputFormat.Json)
            {
                await JsonReportWriter.WriteAsync(ReportDocument.Failure(OperationName(options.Command), exception.Message), stdout, CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                await stderr.WriteLineAsync($"Error: {exception.Message}").ConfigureAwait(false);
            }

            return 1;
        }
    }

    private static async Task<int> RunCleanAsync(
        CliOptions options,
        DevCleanerConfig config,
        AppRuntime runtime,
        TextReader input,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        var roots = ResolveRoots(options.Roots, config.Roots, runtime.HomeDirectory);
        var exclusions = config.Excludes.Concat(options.Exclusions).ToArray();
        var git = new GitClient(runtime.GitExecutable);
        await git.GetVersionAsync(cancellationToken).ConfigureAwait(false);
        var showProgress = runtime.IsErrorInteractive && !options.NoProgress && !options.Quiet && options.OutputFormat != OutputFormat.Json;
        if (showProgress) await stderr.WriteLineAsync($"Scanning {roots.Count} root(s) before cleanup...").ConfigureAwait(false);

        var discoveryService = runtime.DriveRootProvider is null
            ? new RepositoryDiscovery(git)
            : new RepositoryDiscovery(git, runtime.DriveRootProvider);
        var discovery = await discoveryService
            .DiscoverAsync(roots, exclusions, options.AllDrives, cancellationToken)
            .ConfigureAwait(false);
        var ruleCatalog = RuleCatalog.Create(config);
        var scanOptions = new ScanOptions(options.Repositories, options.Categories, exclusions, options.MinimumBytes);
        var scan = await new RepositoryScanner(git)
            .ScanAsync(discovery.Repositories, ruleCatalog, scanOptions, cancellationToken)
            .ConfigureAwait(false);
        var operationWarnings = discovery.Warnings.Concat(scan.Warnings).ToArray();
        var effectiveRoots = discovery.EffectiveRoots ?? roots;
        var availableRepositories = scan.Repositories.Where(repository => repository.Candidates.Count > 0).ToArray();

        IReadOnlyList<ArtifactCandidate> selectedCandidates;
        if (!options.Yes && !options.DryRun && availableRepositories.Length > 0)
        {
            HumanReportWriter.WriteRepositorySelection(availableRepositories, stdout);
            var repositoryDefaults = Enumerable.Range(0, availableRepositories.Length).ToArray();
            var repositorySelection = await ReadSelectionAsync(
                input,
                stdout,
                "Select repositories [Enter=all]: ",
                availableRepositories.Length,
                repositoryDefaults,
                cancellationToken).ConfigureAwait(false);
            var selectedRepositories = repositorySelection.Select(index => availableRepositories[index]).ToArray();
            var availableCandidates = selectedRepositories.SelectMany(repository => repository.Candidates).ToArray();
            HumanReportWriter.WriteCandidateSelection(availableCandidates, stdout);
            var includeOptIn = options.All || options.Categories.Count > 0;
            var candidateDefaults = FilterCandidates(selectedRepositories, includeOptIn)
                .Select(candidate => Array.IndexOf(availableCandidates, candidate))
                .ToArray();
            var candidateSelection = await ReadSelectionAsync(
                input,
                stdout,
                "Select artifacts [Enter=defaults, all=include dependencies]: ",
                availableCandidates.Length,
                candidateDefaults,
                cancellationToken).ConfigureAwait(false);
            selectedCandidates = Array.AsReadOnly(candidateSelection.Select(index => availableCandidates[index]).ToArray());
        }
        else
        {
            var includeOptIn = options.All || options.Categories.Count > 0;
            selectedCandidates = FilterCandidates(availableRepositories, includeOptIn);
        }

        if (!options.Yes && !options.DryRun && selectedCandidates.Count > 0)
        {
            await stdout.WriteAsync($"Type delete to permanently remove {selectedCandidates.Count} selected artifact(s): ").ConfigureAwait(false);
            var confirmation = await input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (!string.Equals(confirmation, "delete", StringComparison.Ordinal))
            {
                await stdout.WriteLineAsync("Cleanup cancelled; nothing was deleted.").ConfigureAwait(false);
                return 0;
            }
        }

        var cleanup = await new CleanupService(git)
            .ExecuteAsync(new CleanupRequest(selectedCandidates, effectiveRoots, ruleCatalog, options.DryRun), cancellationToken)
            .ConfigureAwait(false);
        var report = ReportDocument.FromCleanup(effectiveRoots, cleanup, operationWarnings);
        if (options.OutputFormat == OutputFormat.Json)
        {
            await JsonReportWriter.WriteAsync(report, stdout, CancellationToken.None).ConfigureAwait(false);
        }
        else
        {
            HumanReportWriter.WriteCleanup(
                report,
                stdout,
                new HumanReportOptions(
                    Details: false,
                    Quiet: options.Quiet,
                    Verbose: options.Verbose,
                    UseColor: runtime.IsOutputInteractive && !options.NoColor));
        }

        if (showProgress) await stderr.WriteLineAsync("Cleanup complete.").ConfigureAwait(false);
        if (cleanup.IsInterrupted) return 130;
        var hasSafetySkips = cleanup.Items.Any(item =>
            item.Outcome == CleanupOutcome.Skipped &&
            !(cleanup.DryRun && item.Message.StartsWith("Validated; dry run", StringComparison.Ordinal)));
        return cleanup.FailedCount > 0 || hasSafetySkips || operationWarnings.Length > 0 ? 3 : 0;
    }

    private static IReadOnlyList<ArtifactCandidate> FilterCandidates(
        IReadOnlyList<RepositoryScanResult> repositories,
        bool includeOptIn) =>
        Array.AsReadOnly(repositories
            .SelectMany(repository => repository.Candidates)
            .Where(candidate => includeOptIn || candidate.Preselected)
            .ToArray());

    private static async Task<IReadOnlyList<int>> ReadSelectionAsync(
        TextReader input,
        TextWriter output,
        string prompt,
        int itemCount,
        IReadOnlyList<int> defaultIndices,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await output.WriteAsync(prompt).ConfigureAwait(false);
            var value = await input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            var result = SelectionParser.Parse(value, itemCount, defaultIndices);
            if (result.IsSuccess) return result.SelectedIndices;
            await output.WriteLineAsync($"Invalid selection: {result.Error}").ConfigureAwait(false);
        }
    }

    private static async Task<int> RunScanAsync(
        CliOptions options,
        DevCleanerConfig config,
        AppRuntime runtime,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        var roots = ResolveRoots(options.Roots, config.Roots, runtime.HomeDirectory);
        var exclusions = config.Excludes.Concat(options.Exclusions).ToArray();
        var git = new GitClient(runtime.GitExecutable);
        await git.GetVersionAsync(cancellationToken).ConfigureAwait(false);

        var showProgress = runtime.IsErrorInteractive && !options.NoProgress && !options.Quiet && options.OutputFormat != OutputFormat.Json;
        if (showProgress) await stderr.WriteLineAsync($"Scanning {roots.Count} root(s)...").ConfigureAwait(false);
        var discoveryService = runtime.DriveRootProvider is null
            ? new RepositoryDiscovery(git)
            : new RepositoryDiscovery(git, runtime.DriveRootProvider);
        var discovery = await discoveryService
            .DiscoverAsync(roots, exclusions, options.AllDrives, cancellationToken)
            .ConfigureAwait(false);
        var scanOptions = new ScanOptions(options.Repositories, options.Categories, exclusions, options.MinimumBytes);
        var result = await new RepositoryScanner(git)
            .ScanAsync(discovery.Repositories, RuleCatalog.Create(config), scanOptions, cancellationToken)
            .ConfigureAwait(false);
        if (discovery.Warnings.Count > 0)
        {
            result = result with { Warnings = Array.AsReadOnly(discovery.Warnings.Concat(result.Warnings).ToArray()) };
        }

        var report = ReportDocument.FromScan(discovery.EffectiveRoots ?? roots, result);
        if (options.OutputFormat == OutputFormat.Json)
        {
            await JsonReportWriter.WriteAsync(report, stdout, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            HumanReportWriter.WriteScan(
                report,
                stdout,
                new HumanReportOptions(
                    options.Details,
                    options.Quiet,
                    options.Verbose,
                    runtime.IsOutputInteractive && !options.NoColor));
        }

        if (showProgress) await stderr.WriteLineAsync("Scan complete.").ConfigureAwait(false);
        return report.Warnings.Count == 0 ? 0 : 3;
    }

    private static IReadOnlyList<string> ResolveRoots(
        IReadOnlyList<string> commandLineRoots,
        IReadOnlyList<string> configuredRoots,
        string homeDirectory)
    {
        var selected = commandLineRoots.Count > 0
            ? commandLineRoots
            : configuredRoots.Count > 0
                ? configuredRoots
                : [string.IsNullOrWhiteSpace(homeDirectory) ? Directory.GetCurrentDirectory() : homeDirectory];
        return Array.AsReadOnly(selected
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(Path.GetFullPath)
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .ToArray());
    }

    private static string ResolveConfigPath(string? path) =>
        Path.GetFullPath(string.IsNullOrWhiteSpace(path) ? ConfigLoader.GetDefaultPath() : path);

    private static async Task WriteConfigAsync(DevCleanerConfig config, TextWriter output, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var serializerOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        };
        serializerOptions.Converters.Add(new JsonStringEnumConverter<ArtifactCategory>(namingPolicy: null, allowIntegerValues: false));
        var jsonContext = new DevCleanerJsonContext(serializerOptions);
        var json = JsonSerializer.Serialize(config, jsonContext.DevCleanerConfig);
        await output.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
    }

    private static string OperationName(CommandKind command) => command switch
    {
        CommandKind.RulesList => "rules.list",
        CommandKind.ConfigPath => "config.path",
        CommandKind.ConfigShow => "config.show",
        CommandKind.ConfigValidate => "config.validate",
        _ => command.ToString().ToLowerInvariant(),
    };

    private static string GetVersion()
    {
        var assembly = typeof(DevCleanerApp).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational)) return informational.Split('+')[0];
        return assembly.GetName().Version?.ToString(3) ?? "1.0.0";
    }

    private static void WriteHelp(TextWriter output)
    {
        output.WriteLine("DevCleaner - find regenerable Git-ignored development artifacts");
        output.WriteLine();
        output.WriteLine("Usage:");
        output.WriteLine("  devcleaner scan [root ...] [options]");
        output.WriteLine("  devcleaner clean [root ...] [options]");
        output.WriteLine("  devcleaner rules list [--format table|json] [--config path]");
        output.WriteLine("  devcleaner config path|show|validate [--config path]");
        output.WriteLine("  devcleaner help | --help | version | --version");
        output.WriteLine();
        output.WriteLine("Scan options: --repo name --category value --exclude path --min-size size");
        output.WriteLine("              --all-drives --details --format table|json");
        output.WriteLine("Clean options: --dry-run --yes --all (unattended --yes also requires a scope)");
        output.WriteLine("Console:      --quiet --verbose --no-color --no-progress");
    }
}
