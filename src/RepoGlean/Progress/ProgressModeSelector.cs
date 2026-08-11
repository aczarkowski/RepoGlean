using RepoGlean.Cli;

namespace RepoGlean.Progress;

internal static class ProgressModeSelector
{
    public static ProgressMode Select(
        bool isErrorInteractive,
        OutputFormat format,
        bool quiet,
        bool verbose,
        bool noProgress)
    {
        if (quiet)
        {
            return ProgressMode.None;
        }

        if (verbose)
        {
            return ProgressMode.Verbose;
        }

        if (format == OutputFormat.Json || noProgress || !isErrorInteractive)
        {
            return ProgressMode.None;
        }

        return ProgressMode.Interactive;
    }
}
