using RepoGlean.Auditing;

namespace RepoGlean.Tests.Auditing;

public sealed class IterativeAuditTraversalTests
{
    [Fact]
    public async Task TraverseAsync_completes_a_ten_thousand_level_chain_in_post_order()
    {
        var completed = new List<int>();

        await IterativeAuditTraversal.TraverseAsync(
            root: 0,
            enterAsync: (depth, _) => ValueTask.FromResult<IReadOnlyList<int>>(
                depth == 9_999 ? [] : [depth + 1]),
            complete: completed.Add,
            CancellationToken.None);

        Assert.Equal(10_000, completed.Count);
        Assert.Equal(9_999, completed[0]);
        Assert.Equal(0, completed[^1]);
    }

    [Fact]
    public async Task TraverseAsync_honors_cancellation_between_work_items()
    {
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            IterativeAuditTraversal.TraverseAsync(
                root: 0,
                enterAsync: (depth, _) =>
                {
                    cancellation.Cancel();
                    return ValueTask.FromResult<IReadOnlyList<int>>([depth + 1]);
                },
                complete: _ => { },
                cancellation.Token));
    }
}
