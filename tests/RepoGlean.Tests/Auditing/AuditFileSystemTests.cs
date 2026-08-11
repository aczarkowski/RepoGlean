using RepoGlean.Auditing;
using RepoGlean.Scanning;
using RepoGlean.Tests.Support;

namespace RepoGlean.Tests.Auditing;

public sealed class AuditFileSystemTests
{
    [Fact]
    public void NormalizeRootPath_preserves_a_filesystem_root_and_trims_other_trailing_separators()
    {
        var filesystemRoot = Path.GetPathRoot(Path.GetFullPath("."))!;
        using var temporary = new TemporaryDirectory();

        Assert.Equal(filesystemRoot, AuditFileSystem.NormalizeRootPath(filesystemRoot));
        Assert.Equal(
            Path.GetFullPath(temporary.Path),
            AuditFileSystem.NormalizeRootPath(temporary.Path + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void TryEnumerate_returns_immediate_portable_snapshots_only()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.GetPath("root");
        Directory.CreateDirectory(Path.Combine(root, "child"));
        File.WriteAllBytes(Path.Combine(root, "payload.bin"), new byte[17]);
        File.WriteAllBytes(Path.Combine(root, "child", "nested.bin"), new byte[29]);

        var success = new AuditFileSystem().TryEnumerate(
            root,
            CancellationToken.None,
            out var entries,
            out var error);

        Assert.True(success, error);
        Assert.Collection(
            entries.OrderBy(static entry => entry.Name, StringComparer.Ordinal),
            child =>
            {
                Assert.Equal("child", child.Name);
                Assert.Equal(FileSystemEntryKind.Directory, child.Kind);
                Assert.Null(child.InspectionError);
            },
            payload =>
            {
                Assert.Equal("payload.bin", payload.Name);
                Assert.Equal(FileSystemEntryKind.RegularFile, payload.Kind);
                Assert.Equal(17, payload.Length);
                Assert.NotNull(payload.LastWriteTimeUtc);
                Assert.Null(payload.InspectionError);
            });
        Assert.DoesNotContain(entries, entry => entry.Name == "nested.bin");
    }

    [Fact]
    public void TryInspect_classifies_a_symbolic_link_without_following_it()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var temporary = new TemporaryDirectory();
        var target = temporary.GetPath("target");
        Directory.CreateDirectory(target);
        var link = temporary.GetPath("link");
        Directory.CreateSymbolicLink(link, target);

        var success = new AuditFileSystem().TryInspect(link, out var entry, out var error);

        Assert.True(success, error);
        Assert.NotNull(entry);
        Assert.Equal(FileSystemEntryKind.Link, entry.Kind);
        Assert.Equal(0, entry.Length);
    }

    [Fact]
    public void TryInspect_reports_a_missing_entry_without_throwing()
    {
        using var temporary = new TemporaryDirectory();
        var missing = temporary.GetPath("missing");

        var success = new AuditFileSystem().TryInspect(missing, out var entry, out var error);

        Assert.False(success);
        Assert.Null(entry);
        Assert.NotNull(error);
    }

    [Fact]
    public void TryEnumerate_honors_cancellation_during_materialization()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.GetPath("root");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "one.bin"), "one");
        File.WriteAllText(Path.Combine(root, "two.bin"), "two");
        using var cancellation = new CancellationTokenSource();
        var fileSystem = new AuditFileSystem(new CancellingTimestampProvider(cancellation));

        Assert.Throws<OperationCanceledException>(() =>
            fileSystem.TryEnumerate(root, cancellation.Token, out _, out _));
    }

    private sealed class CancellingTimestampProvider(CancellationTokenSource cancellation) : IFileTimestampProvider
    {
        public bool TryGetLastWriteTimeUtc(string path, out DateTimeOffset value)
        {
            value = DateTimeOffset.UnixEpoch;
            cancellation.Cancel();
            return true;
        }
    }
}
