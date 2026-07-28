using RepoGlean.Progress;

namespace RepoGlean.Tests.Support;

internal sealed class RecordingProgress : IOperationProgress
{
    private readonly List<OperationProgressEvent> events = [];

    public IReadOnlyList<OperationProgressEvent> Events => events;

    public int PauseCount { get; private set; }

    public int ResumeCount { get; private set; }

    public void Report(OperationProgressEvent progressEvent)
    {
        ArgumentNullException.ThrowIfNull(progressEvent);
        events.Add(progressEvent);
    }

    public void Pause() => PauseCount++;

    public void Resume() => ResumeCount++;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
