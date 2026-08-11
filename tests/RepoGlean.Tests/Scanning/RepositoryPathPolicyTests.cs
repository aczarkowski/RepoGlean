using RepoGlean.Scanning;

namespace RepoGlean.Tests.Scanning;

public sealed class RepositoryPathPolicyTests
{
    [Fact]
    public void Visible_content_prefix_uses_the_supplied_platform_comparison()
    {
        string[] visiblePaths = ["Source/Tracked.cs"];

        Assert.True(RepositoryPathPolicy.ContainsVisibleContent(
            "source",
            visiblePaths,
            StringComparison.OrdinalIgnoreCase));
        Assert.False(RepositoryPathPolicy.ContainsVisibleContent(
            "source",
            visiblePaths,
            StringComparison.Ordinal));
    }

    [Fact]
    public void Exact_visible_path_index_has_hash_lookup_equality_cost()
    {
        var comparer = new CountingStringComparer(StringComparer.Ordinal);
        var paths = Enumerable.Range(0, 4096).Select(index => $"tree/{index:D5}.bin").ToArray();
        var visible = RepositoryPathPolicy.CreateVisiblePathSet(paths, comparer);
        var afterConstruction = comparer.EqualityCallCount;

        foreach (var path in paths)
        {
            Assert.Contains(path, visible);
        }

        for (var index = 0; index < paths.Length; index++)
        {
            Assert.DoesNotContain($"missing/{index:D5}.bin", visible);
        }

        Assert.True(
            comparer.EqualityCallCount - afterConstruction < paths.Length * 4,
            $"Expected hash lookup cost, observed {comparer.EqualityCallCount - afterConstruction} equality calls.");
    }

    private sealed class CountingStringComparer(StringComparer inner) : StringComparer
    {
        internal int EqualityCallCount { get; private set; }

        public override int Compare(string? x, string? y) => inner.Compare(x, y);

        public override bool Equals(string? x, string? y)
        {
            EqualityCallCount++;
            return inner.Equals(x, y);
        }

        public override int GetHashCode(string obj) => inner.GetHashCode(obj);
    }
}
