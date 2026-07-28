using RepoGlean.Cli;
using RepoGlean.Progress;

namespace RepoGlean.Tests.Progress;

public sealed class ProgressModeSelectorTests
{
    [Theory]
    [InlineData(true, OutputFormat.Table, false, false, false, (int)ProgressMode.Interactive)]
    [InlineData(true, OutputFormat.Table, false, false, true, (int)ProgressMode.None)]
    [InlineData(false, OutputFormat.Table, false, false, false, (int)ProgressMode.None)]
    [InlineData(true, OutputFormat.Json, false, false, false, (int)ProgressMode.None)]
    [InlineData(false, OutputFormat.Json, false, true, false, (int)ProgressMode.Verbose)]
    [InlineData(false, OutputFormat.Table, false, true, false, (int)ProgressMode.Verbose)]
    [InlineData(false, OutputFormat.Table, false, true, true, (int)ProgressMode.Verbose)]
    [InlineData(true, OutputFormat.Table, true, true, false, (int)ProgressMode.None)]
    public void Select_applies_quiet_verbose_json_interactivity_and_no_progress_precedence(
        bool isErrorInteractive,
        OutputFormat format,
        bool quiet,
        bool verbose,
        bool noProgress,
        int expected)
    {
        var actual = ProgressModeSelector.Select(
            isErrorInteractive,
            format,
            quiet,
            verbose,
            noProgress);

        Assert.Equal((ProgressMode)expected, actual);
    }
}
