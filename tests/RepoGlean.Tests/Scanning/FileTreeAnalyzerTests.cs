using System.Runtime.InteropServices;
using RepoGlean.Scanning;
using RepoGlean.Tests.Support;

namespace RepoGlean.Tests.Scanning;

public sealed class FileTreeAnalyzerTests
{
    [Fact]
    public void Analyze_captures_replacement_resistant_volume_and_file_identity()
    {
        using var temporary = new TemporaryDirectory();
        var candidate = temporary.GetPath("candidate");
        Directory.CreateDirectory(candidate);
        var analyzer = new FileTreeAnalyzer();

        var original = analyzer.Analyze(candidate, temporary.Path);
        Directory.Move(candidate, temporary.GetPath("original-held-open"));
        Directory.CreateDirectory(candidate);
        var replacement = analyzer.Analyze(candidate, temporary.Path);

        Assert.True(original.IsSafe);
        Assert.True(replacement.IsSafe);
        Assert.NotNull(original.Identity);
        Assert.NotNull(replacement.Identity);
        Assert.Equal(original.Identity.VolumeId, replacement.Identity.VolumeId);
        Assert.Equal(original.Identity.MountId, replacement.Identity.MountId);
        Assert.NotEqual(original.Identity.FileId, replacement.Identity.FileId);
    }

    [Fact]
    public void Analyze_fails_closed_when_stable_identity_is_unavailable()
    {
        using var temporary = new TemporaryDirectory();
        var candidate = temporary.GetPath("candidate");
        Directory.CreateDirectory(candidate);
        var analyzer = new FileTreeAnalyzer(new UnavailableIdentityProvider());

        var result = analyzer.Analyze(candidate, temporary.Path);

        Assert.False(result.IsSafe);
        Assert.Null(result.Identity);
        Assert.Contains(result.Warnings, warning => warning.Message.Contains("identity", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Analyze_rejects_a_candidate_root_on_a_different_mount_from_the_repository()
    {
        using var temporary = new TemporaryDirectory();
        var candidate = temporary.GetPath("candidate");
        Directory.CreateDirectory(candidate);
        var analyzer = new FileTreeAnalyzer(new TestIdentityProvider(candidate));

        var result = analyzer.Analyze(candidate, temporary.Path);

        Assert.False(result.IsSafe);
        Assert.Contains(result.Warnings, warning =>
            warning.Path == candidate &&
            warning.Message.Contains("mount", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Analyze_rejects_an_entire_candidate_when_a_nested_directory_changes_mount()
    {
        using var temporary = new TemporaryDirectory();
        var candidate = temporary.GetPath("candidate");
        var nestedMount = System.IO.Path.Combine(candidate, "nested-mount");
        Directory.CreateDirectory(nestedMount);
        File.WriteAllText(System.IO.Path.Combine(nestedMount, "data.bin"), "data");
        var analyzer = new FileTreeAnalyzer(new TestIdentityProvider(nestedMount));

        var result = analyzer.Analyze(candidate, temporary.Path);

        Assert.False(result.IsSafe);
        Assert.Contains(result.Warnings, warning =>
            warning.Path == nestedMount &&
            warning.Message.Contains("mount", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(Architecture.Arm64, (int)MacStatInteropKind.Arm64Unsuffixed)]
    [InlineData(Architecture.X64, (int)MacStatInteropKind.X64Inode64)]
    [InlineData(Architecture.Arm, (int)MacStatInteropKind.Unsupported)]
    [InlineData(Architecture.Wasm, (int)MacStatInteropKind.Unsupported)]
    public void SelectMacStatInterop_uses_the_architecture_correct_entry_point(
        Architecture architecture,
        int expected)
    {
        Assert.Equal((MacStatInteropKind)expected, FileSystemIdentityProvider.SelectMacStatInterop(architecture));
    }

    [Theory]
    [InlineData(0x1100u, true)]
    [InlineData(0x0100u, false)]
    [InlineData(0x1000u, false)]
    [InlineData(0u, false)]
    public void Linux_identity_requires_both_inode_and_mount_id_masks(uint mask, bool expected)
    {
        Assert.Equal(expected, FileSystemIdentityProvider.HasRequiredLinuxIdentity(mask));
    }

    private sealed class UnavailableIdentityProvider : IFileSystemIdentityProvider
    {
        public bool TryGetIdentity(string path, out FileSystemIdentity? identity, out string? error)
        {
            identity = null;
            error = "Stable filesystem identity is unavailable for this test.";
            return false;
        }

        public bool TryGetMountIdentity(string path, out FileSystemMountIdentity? identity, out string? error)
        {
            identity = null;
            error = "Stable filesystem identity is unavailable for this test.";
            return false;
        }
    }

    private sealed class TestIdentityProvider(string foreignMountRoot) : IFileSystemIdentityProvider
    {
        public bool TryGetIdentity(string path, out FileSystemIdentity? identity, out string? error)
        {
            var attributes = File.GetAttributes(path);
            var mountId = RepositoryDiscovery.IsSameOrDescendant(System.IO.Path.GetFullPath(path), System.IO.Path.GetFullPath(foreignMountRoot))
                ? "foreign-mount"
                : "repository-mount";
            identity = new FileSystemIdentity(1, unchecked((ulong)System.IO.Path.GetFullPath(path).GetHashCode()), mountId, attributes, null);
            error = null;
            return true;
        }

        public bool TryGetMountIdentity(string path, out FileSystemMountIdentity? identity, out string? error)
        {
            TryGetIdentity(path, out var fileIdentity, out error);
            identity = fileIdentity is null ? null : new FileSystemMountIdentity(fileIdentity.VolumeId, fileIdentity.MountId);
            return identity is not null;
        }
    }
}
