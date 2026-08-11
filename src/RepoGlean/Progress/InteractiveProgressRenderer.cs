namespace RepoGlean.Progress;

internal interface IProgressTicker : IAsyncDisposable
{
    ValueTask<bool> WaitForNextTickAsync(CancellationToken cancellationToken);
}

internal sealed class PeriodicProgressTicker : IProgressTicker
{
    private readonly PeriodicTimer timer = new(TimeSpan.FromMilliseconds(125));

    public ValueTask<bool> WaitForNextTickAsync(CancellationToken cancellationToken) =>
        timer.WaitForNextTickAsync(cancellationToken);

    public ValueTask DisposeAsync()
    {
        timer.Dispose();
        return ValueTask.CompletedTask;
    }
}

internal sealed class InteractiveProgressRenderer : IOperationProgress
{
    private const string OptionalPathSeparator = " • ";

    private static readonly string[] SpinnerFrames =
    [
        "⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏",
    ];

    private readonly TextWriter writer;
    private readonly Func<int?> terminalWidthProvider;
    private readonly IProgressTicker ticker;
    private readonly object sync = new();
    private readonly CancellationTokenSource refreshCancellation = new();
    private readonly Task refreshTask;
    private ProgressSnapshot snapshot = new();
    private Task? disposeTask;
    private int spinnerIndex;
    private int previousRenderedWidth;
    private bool rendered;
    private bool paused;
    private bool awaitingReportAfterResume;
    private bool disabled;
    private bool writerDisabled;
    private bool disposed;

    public InteractiveProgressRenderer(
        TextWriter writer,
        Func<int?> terminalWidthProvider,
        IProgressTicker ticker)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(terminalWidthProvider);
        ArgumentNullException.ThrowIfNull(ticker);

        this.writer = writer;
        this.terminalWidthProvider = terminalWidthProvider;
        this.ticker = ticker;
        refreshTask = RefreshAsync();
    }

    public void Report(OperationProgressEvent progressEvent)
    {
        ArgumentNullException.ThrowIfNull(progressEvent);

        lock (sync)
        {
            if (disposed)
            {
                return;
            }

            snapshot = snapshot.Apply(progressEvent);
            if (awaitingReportAfterResume)
            {
                awaitingReportAfterResume = false;
            }

            if (rendered || paused || disabled)
            {
                return;
            }

            rendered = Render();
        }
    }

    public void Pause()
    {
        lock (sync)
        {
            if (disposed || paused)
            {
                return;
            }

            paused = true;
            Clear();
            rendered = false;
            awaitingReportAfterResume = false;
        }
    }

    public void Resume()
    {
        lock (sync)
        {
            if (disposed || !paused)
            {
                return;
            }

            paused = false;
            awaitingReportAfterResume = true;
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (sync)
        {
            if (disposeTask is not null)
            {
                return new ValueTask(disposeTask);
            }

            disposed = true;
            refreshCancellation.Cancel();
            disposeTask = FinishDisposalAsync();
            return new ValueTask(disposeTask);
        }
    }

    private async Task RefreshAsync()
    {
        try
        {
            while (await ticker.WaitForNextTickAsync(refreshCancellation.Token))
            {
                lock (sync)
                {
                    if (disposed || paused || awaitingReportAfterResume || disabled)
                    {
                        continue;
                    }

                    if (rendered)
                    {
                        spinnerIndex = (spinnerIndex + 1) % SpinnerFrames.Length;
                    }

                    rendered = Render() || rendered;
                }
            }
        }
        catch (OperationCanceledException) when (refreshCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (IsRecoverableProgressException(exception))
        {
            lock (sync)
            {
                disabled = true;
            }
        }
    }

    private async Task FinishDisposalAsync()
    {
        try
        {
            await refreshTask;
        }
        finally
        {
            try
            {
                lock (sync)
                {
                    Clear();
                }
            }
            finally
            {
                try
                {
                    await ticker.DisposeAsync();
                }
                catch (Exception exception) when (IsRecoverableProgressException(exception))
                {
                    lock (sync)
                    {
                        disabled = true;
                    }
                }
                finally
                {
                    refreshCancellation.Dispose();
                }
            }
        }
    }

    private bool Render()
    {
        var status = snapshot.Format();
        if (status.Length == 0)
        {
            return false;
        }

        var content = $"{SpinnerFrames[spinnerIndex]} {status}";
        var optionalPath = ProgressText.Sanitize(snapshot.OptionalPath);
        if (optionalPath.Length > 0)
        {
            content = AppendOptionalPath(content, ProgressText.DisplayPath(optionalPath));
        }

        try
        {
            var paddedContent = content.PadRight(Math.Max(content.Length, previousRenderedWidth));
            writer.Write($"\r{paddedContent}");
            previousRenderedWidth = content.Length;
            return true;
        }
        catch (Exception exception) when (IsRecoverableProgressException(exception))
        {
            disabled = true;
            writerDisabled = true;
            return false;
        }
    }

    private string AppendOptionalPath(string content, string optionalPath)
    {
        var width = TryGetTerminalWidth();
        if (width is null || width <= content.Length + OptionalPathSeparator.Length)
        {
            return content;
        }

        var availablePathWidth = width.Value - content.Length - OptionalPathSeparator.Length;
        if (optionalPath.Length <= availablePathWidth)
        {
            return content + OptionalPathSeparator + optionalPath;
        }

        if (availablePathWidth < 2)
        {
            return content;
        }

        return content + OptionalPathSeparator + optionalPath[..(availablePathWidth - 1)] + "…";
    }

    private int? TryGetTerminalWidth()
    {
        try
        {
            return terminalWidthProvider();
        }
        catch (Exception exception) when (IsRecoverableProgressException(exception))
        {
            return null;
        }
    }

    private void Clear()
    {
        if (writerDisabled || previousRenderedWidth == 0)
        {
            return;
        }

        try
        {
            writer.Write($"\r{new string(' ', previousRenderedWidth)}\r");
            previousRenderedWidth = 0;
        }
        catch (Exception exception) when (IsRecoverableProgressException(exception))
        {
            disabled = true;
            writerDisabled = true;
        }
    }

    private static bool IsRecoverableProgressException(Exception exception) =>
        exception is not OutOfMemoryException
        and not StackOverflowException
        and not AccessViolationException;
}
