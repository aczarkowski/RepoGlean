using System.IO;
using RepoGlean.Progress;

namespace RepoGlean.Tests.Progress;

public sealed class VerboseProgressRendererTests
{
    [Fact]
    public void Report_writes_stable_scan_milestones_in_order()
    {
        using var writer = new StringWriter();
        var renderer = new VerboseProgressRenderer(writer);

        var events = new[]
        {
            new OperationProgressEvent(ProgressEventKind.DiscoveryStarted, ProgressOperation.Scan, Roots: ["/work"]),
            new OperationProgressEvent(ProgressEventKind.DiscoveryCompleted, ProgressOperation.Scan, RepositoryCount: 18),
            new OperationProgressEvent(ProgressEventKind.RepositoryScanStarted, ProgressOperation.Scan, Path: "/work/my-api", Current: 7, Total: 18),
            new OperationProgressEvent(ProgressEventKind.RepositoryScanCompleted, ProgressOperation.Scan, Path: "/work/my-api", Current: 7, Total: 18, CurrentCandidateCount: 3, CurrentEstimatedBytes: 448790528, CandidateCount: 23, EstimatedBytes: 1503238553),
            new OperationProgressEvent(ProgressEventKind.Warning, ProgressOperation.Scan, Path: "/work/unreadable", Message: "Unable to inspect path.", WarningCount: 1),
            new OperationProgressEvent(ProgressEventKind.Completed, ProgressOperation.Scan, RepositoryCount: 18, CandidateCount: 23, EstimatedBytes: 1503238553, WarningCount: 1),
        };

        foreach (var progressEvent in events) renderer.Report(progressEvent);

        AssertContainsInOrder(writer.ToString(),
            "Discovering repositories under /work...",
            "Found 18 repositories.",
            "Scanning [7/18] /work/my-api...",
            "Found 3 candidates in my-api (428 MiB estimated).",
            "Warning: /work/unreadable: Unable to inspect path.",
            "Scan complete: 18 repositories, 23 candidates, 1 warning.");
    }

    [Fact]
    public void Report_writes_cleanup_outcomes_and_interrupted_aggregate_without_terminal_controls()
    {
        using var writer = new StringWriter { NewLine = "\r\n" };
        var renderer = new VerboseProgressRenderer(writer);

        renderer.Report(new OperationProgressEvent(ProgressEventKind.CandidateStarted, ProgressOperation.Clean, Path: "/work/my-api/obj", Current: 2, Total: 6, DryRun: true));
        renderer.Report(new OperationProgressEvent(ProgressEventKind.CandidateCompleted, ProgressOperation.Clean, Path: "/work/my-api/obj", Current: 2, Total: 6, CurrentEstimatedBytes: 132120576, Outcome: ProgressCandidateOutcome.Deleted));
        renderer.Report(new OperationProgressEvent(ProgressEventKind.CandidateCompleted, ProgressOperation.Clean, Path: "/work/my-api/bin", Current: 3, Total: 6, Outcome: ProgressCandidateOutcome.Validated));
        renderer.Report(new OperationProgressEvent(ProgressEventKind.CandidateCompleted, ProgressOperation.Clean, Path: "/work/my-api/cache", Current: 4, Total: 6, Outcome: ProgressCandidateOutcome.Skipped));
        renderer.Report(new OperationProgressEvent(ProgressEventKind.CandidateCompleted, ProgressOperation.Clean, Path: "/work/my-api/tmp", Current: 5, Total: 6, Outcome: ProgressCandidateOutcome.Failed));
        renderer.Report(new OperationProgressEvent(ProgressEventKind.Interrupted, ProgressOperation.Clean, CandidateCount: 6, DeletedCount: 1, ValidatedCount: 1, SkippedCount: 1, FailedCount: 1, WarningCount: 1));

        var output = writer.ToString();
        Assert.Contains("Validating [2/6] /work/my-api/obj...", output, StringComparison.Ordinal);
        Assert.Contains("Deleted my-api/obj (126 MiB estimated).", output, StringComparison.Ordinal);
        Assert.Contains("Validated my-api/bin", output, StringComparison.Ordinal);
        Assert.Contains("Skipped my-api/cache", output, StringComparison.Ordinal);
        Assert.Contains("Failed my-api/tmp", output, StringComparison.Ordinal);
        Assert.Contains("Cleanup interrupted: 1 deleted, 1 validated, 1 skipped, 1 failed, 1 warning.", output, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", output, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Report_disables_rendering_after_a_write_failure()
    {
        using var writer = new ThrowingTextWriter();
        var renderer = new VerboseProgressRenderer(writer);

        var exception = Record.Exception(() =>
        {
            renderer.Report(new OperationProgressEvent(ProgressEventKind.DiscoveryStarted, ProgressOperation.Scan));
            renderer.Report(new OperationProgressEvent(ProgressEventKind.DiscoveryCompleted, ProgressOperation.Scan));
        });

        Assert.Null(exception);
        Assert.Equal(1, writer.WriteAttemptCount);
    }

    [Fact]
    public void Report_labels_terminal_failures_with_the_operation_name()
    {
        using var writer = new StringWriter();
        var renderer = new VerboseProgressRenderer(writer);

        renderer.Report(new OperationProgressEvent(ProgressEventKind.Failed, ProgressOperation.Scan, Message: "Git was unavailable."));
        renderer.Report(new OperationProgressEvent(ProgressEventKind.Failed, ProgressOperation.Clean, Message: "Cleanup was cancelled."));

        AssertContainsInOrder(writer.ToString(),
            "Scan failed: Git was unavailable.",
            "Cleanup failed: Cleanup was cancelled.");
    }

    [Fact]
    public void Report_removes_control_characters_from_event_supplied_text()
    {
        using var writer = new StringWriter { NewLine = "\r\n" };
        var renderer = new VerboseProgressRenderer(writer);

        renderer.Report(new OperationProgressEvent(
            ProgressEventKind.DiscoveryStarted,
            ProgressOperation.Scan,
            Roots: ["/work\r\n\u001b[31mred"]));
        renderer.Report(new OperationProgressEvent(
            ProgressEventKind.RepositoryScanStarted,
            ProgressOperation.Scan,
            Path: "/work/my\r\n\u001b[31m-api",
            Current: 1,
            Total: 1));
        renderer.Report(new OperationProgressEvent(
            ProgressEventKind.Warning,
            ProgressOperation.Scan,
            Path: "/work/unreadable\r\n\u001b[31m",
            Message: "Unable to inspect\r\n\u001b[31m path."));
        renderer.Report(new OperationProgressEvent(
            ProgressEventKind.Failed,
            ProgressOperation.Scan,
            Message: "Git\r\n\u001b[31m failed."));

        var output = writer.ToString();
        Assert.Contains("/work[31mred", output, StringComparison.Ordinal);
        Assert.Contains("/work/my[31m-api", output, StringComparison.Ordinal);
        Assert.Contains("Warning: /work/unreadable[31m: Unable to inspect[31m path.", output, StringComparison.Ordinal);
        Assert.Contains("Scan failed: Git[31m failed.", output, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", output, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b", output, StringComparison.Ordinal);
        Assert.Equal(4, output.Count(character => character == '\n'));
    }

    private static void AssertContainsInOrder(string output, params string[] expectedLines)
    {
        var position = 0;
        foreach (var expectedLine in expectedLines)
        {
            var next = output.IndexOf(expectedLine, position, StringComparison.Ordinal);
            Assert.True(next >= position, $"Expected '{expectedLine}' after position {position} in:{Environment.NewLine}{output}");
            position = next + expectedLine.Length;
        }
    }

    private sealed class ThrowingTextWriter : StringWriter
    {
        public int WriteAttemptCount { get; private set; }

        public override void WriteLine(string? value)
        {
            WriteAttemptCount++;
            throw new IOException("Simulated write failure.");
        }
    }
}
