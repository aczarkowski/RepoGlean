using RepoGlean.Cli;
using RepoGlean.Progress;

namespace RepoGlean.Tests.Progress;

public sealed class ProgressReporterFactoryTests
{
    public static TheoryData<bool, OutputFormat, bool, bool, bool, Type> SelectionRows =>
        new()
        {
            { true, OutputFormat.Table, false, false, false, typeof(InteractiveProgressRenderer) },
            { true, OutputFormat.Table, false, false, true, typeof(NullOperationProgress) },
            { false, OutputFormat.Table, false, false, false, typeof(NullOperationProgress) },
            { true, OutputFormat.Json, false, false, false, typeof(NullOperationProgress) },
            { false, OutputFormat.Json, false, true, false, typeof(VerboseProgressRenderer) },
            { false, OutputFormat.Table, false, true, false, typeof(VerboseProgressRenderer) },
            { false, OutputFormat.Table, false, true, true, typeof(VerboseProgressRenderer) },
            { true, OutputFormat.Table, true, true, false, typeof(NullOperationProgress) },
        };

    [Theory]
    [MemberData(nameof(SelectionRows))]
    public async Task Create_returns_the_renderer_selected_for_every_mode_precedence_row(
        bool isErrorInteractive,
        OutputFormat format,
        bool quiet,
        bool verbose,
        bool noProgress,
        Type expectedType)
    {
        using var writer = new StringWriter();
        var ticker = new ManualProgressTicker();

        var reporter = ProgressReporterFactory.Create(
            isErrorInteractive,
            format,
            quiet,
            verbose,
            noProgress,
            writer,
            () => 120,
            ticker);

        Assert.IsType(expectedType, reporter);
        await reporter.DisposeAsync();
    }
}
