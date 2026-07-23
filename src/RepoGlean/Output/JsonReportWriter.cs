using System.Text.Json;

namespace RepoGlean.Output;

public static class JsonReportWriter
{
    public static async Task WriteAsync(ReportDocument report, TextWriter output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(output);
        cancellationToken.ThrowIfCancellationRequested();
        var json = JsonSerializer.Serialize(report, ReportJsonContext.Default.ReportDocument);
        await output.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
    }
}
