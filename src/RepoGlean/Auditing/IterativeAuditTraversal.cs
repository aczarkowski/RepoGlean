namespace RepoGlean.Auditing;

internal static class IterativeAuditTraversal
{
    internal static async Task TraverseAsync<TFrame>(
        TFrame root,
        Func<TFrame, CancellationToken, ValueTask<IReadOnlyList<TFrame>>> enterAsync,
        Action<TFrame> complete,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(enterAsync);
        ArgumentNullException.ThrowIfNull(complete);
        var work = new Stack<WorkItem<TFrame>>();
        work.Push(new EnterWork<TFrame>(root));
        while (work.TryPop(out var item))
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (item)
            {
                case EnterWork<TFrame> enter:
                    var children = await enterAsync(enter.Frame, cancellationToken).ConfigureAwait(false);
                    work.Push(new CompleteWork<TFrame>(enter.Frame));
                    for (var index = children.Count - 1; index >= 0; index--)
                    {
                        work.Push(new EnterWork<TFrame>(children[index]));
                    }

                    break;
                case CompleteWork<TFrame> completed:
                    complete(completed.Frame);
                    break;
            }
        }
    }

    private abstract record WorkItem<TFrame>(TFrame Frame);

    private sealed record EnterWork<TFrame>(TFrame Frame) : WorkItem<TFrame>(Frame);

    private sealed record CompleteWork<TFrame>(TFrame Frame) : WorkItem<TFrame>(Frame);
}
