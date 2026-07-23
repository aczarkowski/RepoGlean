using DevCleaner.Cleaning;
using DevCleaner.Scanning;
using DevCleaner.Tests.Support;

namespace DevCleaner.Tests.Cleaning;

public sealed class BoundaryAwareDeleterTests
{
    [Fact]
    public void Case_colliding_snapshot_paths_are_rejected_as_a_contained_io_failure()
    {
        var identity = new FileSystemIdentity(1, 1, "mount", FileAttributes.Normal, null);
        var snapshot = new OwnedTreeSnapshot(
        [
            new OwnedTreeEntry(".", identity with { Attributes = FileAttributes.Directory }, OwnedTreeEntryType.Directory),
            new OwnedTreeEntry("a", identity with { FileId = 2 }, OwnedTreeEntryType.File),
            new OwnedTreeEntry("A", identity with { FileId = 3 }, OwnedTreeEntryType.File),
        ]);

        var exception = Assert.Throws<IOException>(() =>
            BoundaryAwareDeleter.BuildEntryMap(snapshot, StringComparer.OrdinalIgnoreCase));

        Assert.Contains("ambiguous", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Child_mount_boundary_is_refused_before_traversal_and_content_survives()
    {
        using var temporary = new TemporaryDirectory();
        var payload = temporary.GetPath("payload");
        var child = Path.Combine(payload, "child");
        Directory.CreateDirectory(child);
        var protectedFile = Path.Combine(child, "protected.bin");
        File.WriteAllText(protectedFile, "protected");
        var fileSystem = new SystemCleanupFileSystem();
        var stableIdentityProvider = new FileSystemIdentityProvider();
        Assert.True(stableIdentityProvider.TryGetIdentity(payload, out var payloadIdentity, out var identityError), identityError);
        Assert.NotNull(payloadIdentity);
        var inspector = new OwnedTreeInspector(fileSystem, stableIdentityProvider);
        Assert.True(
            inspector.TryCapture(payload, payloadIdentity, CancellationToken.None, out var snapshot, out var snapshotError),
            snapshotError);
        Assert.NotNull(snapshot);
        var deleter = new BoundaryAwareDeleter(
            fileSystem,
            new ForeignChildMountIdentityProvider(child));

        var exception = Assert.Throws<IOException>(() =>
            deleter.Delete(payload, snapshot, payloadIdentity, CancellationToken.None));

        Assert.Contains("mount", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(protectedFile));
    }

    private sealed class ForeignChildMountIdentityProvider(string childPath) : IFileSystemIdentityProvider
    {
        private readonly FileSystemIdentityProvider inner = new();

        public bool TryGetIdentity(string path, out FileSystemIdentity? identity, out string? error)
        {
            if (!inner.TryGetIdentity(path, out identity, out error) || identity is null) return false;
            if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(childPath), StringComparison.Ordinal))
            {
                identity = identity with { MountId = "injected-child-mount" };
            }

            return true;
        }

        public bool TryGetMountIdentity(string path, out FileSystemMountIdentity? identity, out string? error)
        {
            if (!TryGetIdentity(path, out var fileIdentity, out error) || fileIdentity is null)
            {
                identity = null;
                return false;
            }

            identity = new FileSystemMountIdentity(fileIdentity.VolumeId, fileIdentity.MountId);
            return true;
        }
    }
}
