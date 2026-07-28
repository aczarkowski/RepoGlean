using RepoGlean.Cli;

namespace RepoGlean.Progress;

internal static class ProgressReporterFactory
{
    internal static IOperationProgress Create(
        bool isErrorInteractive,
        OutputFormat format,
        bool quiet,
        bool verbose,
        bool noProgress,
        TextWriter stderr,
        Func<int?> terminalWidthProvider,
        IProgressTicker? ticker = null) =>
        ProgressModeSelector.Select(isErrorInteractive, format, quiet, verbose, noProgress) switch
        {
            ProgressMode.Interactive => new InteractiveProgressRenderer(
                stderr,
                terminalWidthProvider,
                ticker ?? new PeriodicProgressTicker()),
            ProgressMode.Verbose => new VerboseProgressRenderer(stderr),
            _ => NullOperationProgress.Instance,
        };
}
