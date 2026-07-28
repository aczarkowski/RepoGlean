using System.Text;
using System.Threading.Channels;
using RepoGlean.Progress;

namespace RepoGlean.Tests.Progress;

public sealed class InteractiveProgressRendererTests
{
    [Fact]
    public async Task Report_renders_first_event_synchronously_then_latest_state_on_one_tick()
    {
        using var writer = new LockedStringWriter();
        var ticker = new ManualProgressTicker();
        await using var renderer = new InteractiveProgressRenderer(writer, () => 120, ticker);

        renderer.Report(new OperationProgressEvent(
            ProgressEventKind.DiscoveryStarted,
            ProgressOperation.Scan));

        const string initial = "\r⠋ Discovering repositories • 0 found";
        Assert.Equal(initial, writer.Snapshot);

        renderer.Report(new OperationProgressEvent(
            ProgressEventKind.RepositoryFound,
            ProgressOperation.Scan,
            Path: "/work/my-api",
            RepositoryCount: 12));
        Assert.Equal(initial, writer.Snapshot);

        ticker.Tick();
        await WaitForOutputAsync(
            writer,
            output => output.Contains("\r⠙ Discovering repositories • 12 found", StringComparison.Ordinal));

        Assert.Equal(2, writer.Snapshot.Count(character => character == '\r'));
    }

    [Fact]
    public async Task Resume_waits_for_the_next_event_then_renders_that_state_synchronously()
    {
        using var writer = new LockedStringWriter();
        var ticker = new ManualProgressTicker();
        await using var renderer = new InteractiveProgressRenderer(writer, () => 120, ticker);

        renderer.Report(new OperationProgressEvent(
            ProgressEventKind.DiscoveryStarted,
            ProgressOperation.Scan));
        var renderedWidth = "⠋ Discovering repositories • 0 found".Length;

        renderer.Pause();
        var pausedOutput = writer.Snapshot;
        Assert.EndsWith($"\r{new string(' ', renderedWidth)}\r", pausedOutput, StringComparison.Ordinal);

        renderer.Report(new OperationProgressEvent(
            ProgressEventKind.RepositoryFound,
            ProgressOperation.Scan,
            Path: "/work/my-api",
            RepositoryCount: 12));
        ticker.Tick();
        await Task.Yield();
        Assert.Equal(pausedOutput, writer.Snapshot);

        renderer.Resume();
        Assert.Equal(pausedOutput, writer.Snapshot);
        ticker.Tick();
        await Task.Yield();
        Assert.Equal(pausedOutput, writer.Snapshot);

        renderer.Report(new OperationProgressEvent(
            ProgressEventKind.CandidateStarted,
            ProgressOperation.Clean,
            Path: "/work/my-api/obj",
            Current: 1,
            Total: 2));
        Assert.EndsWith(
            "\r⠋ Cleaning artifacts 1/2 • 0 deleted • 0 B estimated • obj",
            writer.Snapshot,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Report_omits_optional_path_at_width_40_while_retaining_stage_and_counters()
    {
        using var writer = new LockedStringWriter();
        var ticker = new ManualProgressTicker();
        await using var renderer = new InteractiveProgressRenderer(writer, () => 40, ticker);

        renderer.Report(new OperationProgressEvent(
            ProgressEventKind.RepositoryScanStarted,
            ProgressOperation.Scan,
            Path: "/work/a-very-long-repository-name",
            Current: 7,
            Total: 18,
            CandidateCount: 23,
            EstimatedBytes: 1503238553));

        var output = writer.Snapshot;
        Assert.Contains(
            "Scanning repositories 7/18 • 23 candidates • 1.4 GiB estimated",
            output,
            StringComparison.Ordinal);
        Assert.DoesNotContain("a-very-long-repository-name", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Report_truncates_optional_path_with_ellipsis_to_exact_terminal_width()
    {
        const string fixedContent = "⠋ Scanning repositories 1/2 • 3 candidates • 4 KiB estimated";
        const int visiblePathWidth = 8;
        var terminalWidth = fixedContent.Length + " • ".Length + visiblePathWidth;
        using var writer = new LockedStringWriter();
        var ticker = new ManualProgressTicker();
        await using var renderer = new InteractiveProgressRenderer(writer, () => terminalWidth, ticker);

        renderer.Report(new OperationProgressEvent(
            ProgressEventKind.RepositoryScanStarted,
            ProgressOperation.Scan,
            Path: "/work/a-very-long-repository-name",
            Current: 1,
            Total: 2,
            CandidateCount: 3,
            EstimatedBytes: 4 * 1024));

        var renderedLine = writer.Snapshot[(writer.Snapshot.LastIndexOf('\r') + 1)..];
        Assert.StartsWith(fixedContent + " • ", renderedLine, StringComparison.Ordinal);
        Assert.EndsWith("…", renderedLine, StringComparison.Ordinal);
        Assert.DoesNotContain("a-very-long-repository-name", renderedLine, StringComparison.Ordinal);
        Assert.Equal(terminalWidth, renderedLine.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Report_omits_optional_path_when_width_is_unavailable(bool throws)
    {
        using var writer = new LockedStringWriter();
        var ticker = new ManualProgressTicker();
        Func<int?> widthProvider = throws
            ? () => throw new InvalidOperationException("Terminal width unavailable.")
            : () => null;
        await using var renderer = new InteractiveProgressRenderer(writer, widthProvider, ticker);

        var exception = Record.Exception(() => renderer.Report(new OperationProgressEvent(
            ProgressEventKind.RepositoryScanStarted,
            ProgressOperation.Scan,
            Path: "/work/my-api",
            Current: 1,
            Total: 2,
            CandidateCount: 3,
            EstimatedBytes: 4096)));

        Assert.Null(exception);
        Assert.Contains("Scanning repositories 1/2 • 3 candidates • 4 KiB estimated", writer.Snapshot, StringComparison.Ordinal);
        Assert.DoesNotContain("my-api", writer.Snapshot, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Report_disables_later_writes_after_recoverable_writer_failure_without_throwing(
        bool invalidOperation)
    {
        using var writer = new ThrowingTextWriter(
            invalidOperation
                ? new InvalidOperationException("Simulated writer state failure.")
                : new IOException("Simulated write failure."));
        var ticker = new ManualProgressTicker();
        await using var renderer = new InteractiveProgressRenderer(writer, () => 120, ticker);

        var exception = Record.Exception(() =>
        {
            renderer.Report(new OperationProgressEvent(
                ProgressEventKind.DiscoveryStarted,
                ProgressOperation.Scan));
            renderer.Report(new OperationProgressEvent(
                ProgressEventKind.RepositoryFound,
                ProgressOperation.Scan,
                RepositoryCount: 1));
            ticker.Tick();
        });

        await Task.Yield();
        Assert.Null(exception);
        Assert.Equal(1, writer.WriteAttemptCount);
    }

    [Fact]
    public async Task Pause_disables_later_writes_after_recoverable_clear_failure_without_throwing()
    {
        using var writer = new ThrowingTextWriter(
            new InvalidOperationException("Simulated clear failure."),
            failOnAttempt: 2);
        var ticker = new ManualProgressTicker();
        await using var renderer = new InteractiveProgressRenderer(writer, () => 120, ticker);

        renderer.Report(new OperationProgressEvent(
            ProgressEventKind.DiscoveryStarted,
            ProgressOperation.Scan));

        var exception = Record.Exception(() =>
        {
            renderer.Pause();
            renderer.Resume();
        });

        Assert.Null(exception);
        Assert.Equal(2, writer.WriteAttemptCount);
    }

    [Fact]
    public async Task Resume_disables_later_writes_after_recoverable_render_failure_without_throwing()
    {
        using var writer = new ThrowingTextWriter(
            new InvalidOperationException("Simulated resume failure."),
            failOnAttempt: 3);
        var ticker = new ManualProgressTicker();
        await using var renderer = new InteractiveProgressRenderer(writer, () => 120, ticker);

        renderer.Report(new OperationProgressEvent(
            ProgressEventKind.DiscoveryStarted,
            ProgressOperation.Scan));
        renderer.Pause();
        renderer.Report(new OperationProgressEvent(
            ProgressEventKind.RepositoryFound,
            ProgressOperation.Scan,
            RepositoryCount: 1));

        var exception = Record.Exception(() =>
        {
            renderer.Resume();
            renderer.Report(new OperationProgressEvent(
                ProgressEventKind.RepositoryFound,
                ProgressOperation.Scan,
                RepositoryCount: 2));
        });

        Assert.Null(exception);
        Assert.Equal(3, writer.WriteAttemptCount);
    }

    [Fact]
    public async Task DisposeAsync_isolates_recoverable_final_clear_failure_and_still_disposes_ticker_once()
    {
        using var writer = new ThrowingTextWriter(
            new InvalidOperationException("Simulated final clear failure."),
            failOnAttempt: 2);
        var ticker = new ManualProgressTicker();
        var renderer = new InteractiveProgressRenderer(writer, () => 120, ticker);

        renderer.Report(new OperationProgressEvent(
            ProgressEventKind.DiscoveryStarted,
            ProgressOperation.Scan));

        var firstException = await Record.ExceptionAsync(async () => await renderer.DisposeAsync());
        var secondException = await Record.ExceptionAsync(async () => await renderer.DisposeAsync());

        Assert.Null(firstException);
        Assert.Null(secondException);
        Assert.Equal(2, writer.WriteAttemptCount);
        Assert.Equal(1, ticker.DisposeCount);
    }

    [Fact]
    public async Task Report_does_not_swallow_catastrophic_writer_failure()
    {
        using var writer = new ThrowingTextWriter(
            new OutOfMemoryException("Simulated catastrophic failure."));
        var ticker = new ManualProgressTicker();
        await using var renderer = new InteractiveProgressRenderer(writer, () => 120, ticker);

        var exception = Record.Exception(() => renderer.Report(new OperationProgressEvent(
            ProgressEventKind.DiscoveryStarted,
            ProgressOperation.Scan)));

        Assert.IsType<OutOfMemoryException>(exception);
        Assert.Equal(1, writer.WriteAttemptCount);
    }

    [Fact]
    public async Task DisposeAsync_isolates_ticker_wait_failure_and_still_clears_and_disposes_once()
    {
        using var writer = new LockedStringWriter();
        var ticker = new WaitFailingProgressTicker();
        var renderer = new InteractiveProgressRenderer(writer, () => 120, ticker);

        renderer.Report(new OperationProgressEvent(
            ProgressEventKind.DiscoveryStarted,
            ProgressOperation.Scan));
        var beforeFailure = writer.Snapshot;
        var renderedWidth = "⠋ Discovering repositories • 0 found".Length;

        ticker.FailWait();
        await ticker.WaitFailureObserved.WaitAsync(TimeSpan.FromSeconds(5));

        var firstException = await Record.ExceptionAsync(async () => await renderer.DisposeAsync());
        var secondException = await Record.ExceptionAsync(async () => await renderer.DisposeAsync());

        Assert.Null(firstException);
        Assert.Null(secondException);
        Assert.Equal(
            beforeFailure + $"\r{new string(' ', renderedWidth)}\r",
            writer.Snapshot);
        Assert.Equal(1, ticker.DisposeCount);
    }

    [Fact]
    public async Task DisposeAsync_isolates_ticker_disposal_failure_after_cancellation_and_clear()
    {
        using var writer = new LockedStringWriter();
        var ticker = new DisposeFailingProgressTicker();
        var renderer = new InteractiveProgressRenderer(writer, () => 120, ticker);

        renderer.Report(new OperationProgressEvent(
            ProgressEventKind.DiscoveryStarted,
            ProgressOperation.Scan));
        var beforeDispose = writer.Snapshot;
        var renderedWidth = "⠋ Discovering repositories • 0 found".Length;

        var firstException = await Record.ExceptionAsync(async () => await renderer.DisposeAsync());
        var secondException = await Record.ExceptionAsync(async () => await renderer.DisposeAsync());

        Assert.Null(firstException);
        Assert.Null(secondException);
        Assert.True(ticker.WaitCancellationObserved);
        Assert.Equal(
            beforeDispose + $"\r{new string(' ', renderedWidth)}\r",
            writer.Snapshot);
        Assert.Equal(1, ticker.DisposeCount);
    }

    [Fact]
    public async Task DisposeAsync_cancels_refresh_clears_once_and_disposes_ticker_once()
    {
        using var writer = new LockedStringWriter();
        var ticker = new ManualProgressTicker();
        var renderer = new InteractiveProgressRenderer(writer, () => 120, ticker);

        renderer.Report(new OperationProgressEvent(
            ProgressEventKind.DiscoveryStarted,
            ProgressOperation.Scan));
        var beforeDispose = writer.Snapshot;
        var renderedWidth = "⠋ Discovering repositories • 0 found".Length;

        await renderer.DisposeAsync();

        Assert.Equal(
            beforeDispose + $"\r{new string(' ', renderedWidth)}\r",
            writer.Snapshot);
        Assert.Equal(1, ticker.DisposeCount);

        var disposedOutput = writer.Snapshot;
        ticker.Tick();
        renderer.Report(new OperationProgressEvent(
            ProgressEventKind.RepositoryFound,
            ProgressOperation.Scan,
            RepositoryCount: 99));
        await renderer.DisposeAsync();
        await Task.Yield();

        Assert.Equal(disposedOutput, writer.Snapshot);
        Assert.Equal(1, ticker.DisposeCount);
    }

    private static async Task WaitForOutputAsync(
        LockedStringWriter writer,
        Func<string, bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate(writer.Snapshot))
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class LockedStringWriter : TextWriter
    {
        private readonly object sync = new();
        private readonly StringBuilder builder = new();

        public override Encoding Encoding => Encoding.UTF8;

        public string Snapshot
        {
            get
            {
                lock (sync)
                {
                    return builder.ToString();
                }
            }
        }

        public override void Write(string? value)
        {
            lock (sync)
            {
                builder.Append(value);
            }
        }
    }

    private sealed class ThrowingTextWriter(
        Exception exception,
        int failOnAttempt = 1) : StringWriter
    {
        public int WriteAttemptCount { get; private set; }

        public override void Write(string? value)
        {
            WriteAttemptCount++;
            if (WriteAttemptCount == failOnAttempt)
            {
                throw exception;
            }

            base.Write(value);
        }
    }
}

internal sealed class ManualProgressTicker : IProgressTicker
{
    private readonly Channel<bool> ticks = Channel.CreateUnbounded<bool>();

    public int DisposeCount { get; private set; }

    public void Tick() => ticks.Writer.TryWrite(true);

    public async ValueTask<bool> WaitForNextTickAsync(CancellationToken cancellationToken) =>
        await ticks.Reader.ReadAsync(cancellationToken);

    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        ticks.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}

internal sealed class WaitFailingProgressTicker : IProgressTicker
{
    private readonly TaskCompletionSource failWait =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource waitFailureObserved =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int DisposeCount { get; private set; }

    public Task WaitFailureObserved => waitFailureObserved.Task;

    public void FailWait() => failWait.TrySetResult();

    public async ValueTask<bool> WaitForNextTickAsync(CancellationToken cancellationToken)
    {
        await failWait.Task.WaitAsync(cancellationToken);
        waitFailureObserved.TrySetResult();
        throw new InvalidOperationException("Simulated ticker wait failure.");
    }

    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        return ValueTask.CompletedTask;
    }
}

internal sealed class DisposeFailingProgressTicker : IProgressTicker
{
    public int DisposeCount { get; private set; }

    public bool WaitCancellationObserved { get; private set; }

    public async ValueTask<bool> WaitForNextTickAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return false;
        }
        finally
        {
            WaitCancellationObserved = cancellationToken.IsCancellationRequested;
        }
    }

    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        return ValueTask.FromException(
            new InvalidOperationException("Simulated ticker disposal failure."));
    }
}
