namespace RepoGlean.Progress;

internal interface IOperationProgress : IAsyncDisposable
{
    void Report(OperationProgressEvent progressEvent);

    void Pause();

    void Resume();
}

internal sealed class NullOperationProgress : IOperationProgress
{
    public static NullOperationProgress Instance { get; } = new();

    private NullOperationProgress()
    {
    }

    public void Report(OperationProgressEvent progressEvent)
    {
        ArgumentNullException.ThrowIfNull(progressEvent);
    }

    public void Pause()
    {
    }

    public void Resume()
    {
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
