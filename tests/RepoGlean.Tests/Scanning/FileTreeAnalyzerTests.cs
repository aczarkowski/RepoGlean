using System.Runtime.InteropServices;
using RepoGlean.Cleaning;
using RepoGlean.Scanning;
using RepoGlean.Tests.Support;

namespace RepoGlean.Tests.Scanning;

public sealed class FileTreeAnalyzerTests
{
    [Fact]
    public void Analyze_records_the_newest_root_or_descendant_write_time()
    {
        using var temporary = new TemporaryDirectory();
        var repository = temporary.GetPath("repo");
        var candidate = Path.Combine(repository, "obj");
        Directory.CreateDirectory(candidate);
        File.WriteAllText(Path.Combine(candidate, "old.bin"), "old");
        File.WriteAllText(Path.Combine(candidate, "new.bin"), "new");
        var oldTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var newTime = oldTime.AddDays(1);
        var timestamps = new StubTimestampProvider(new Dictionary<string, DateTimeOffset>
        {
            [Path.GetFullPath(candidate)] = oldTime,
            [Path.GetFullPath(Path.Combine(candidate, "old.bin"))] = oldTime,
            [Path.GetFullPath(Path.Combine(candidate, "new.bin"))] = newTime,
        });
        var analyzer = new FileTreeAnalyzer(new FileSystemIdentityProvider(), timestamps);

        var result = analyzer.Analyze(candidate, repository);

        Assert.True(result.IsSafe);
        Assert.Equal(newTime, result.NewestWriteTimeUtc);
    }

    [Fact]
    public void Analyze_uses_null_when_any_timestamp_is_unavailable()
    {
        using var temporary = new TemporaryDirectory();
        var repository = temporary.GetPath("repo");
        var candidate = Path.Combine(repository, "obj");
        Directory.CreateDirectory(candidate);
        var missingTimestamp = Path.Combine(candidate, "artifact.bin");
        File.WriteAllText(missingTimestamp, "payload");
        var timestamps = new StubTimestampProvider(
            new Dictionary<string, DateTimeOffset>
            {
                [Path.GetFullPath(candidate)] = DateTimeOffset.UnixEpoch,
            });
        var analyzer = new FileTreeAnalyzer(new FileSystemIdentityProvider(), timestamps);

        var result = analyzer.Analyze(candidate, repository);

        Assert.True(result.IsSafe);
        Assert.Null(result.NewestWriteTimeUtc);
    }

    [Fact]
    public void Analyze_records_size_and_timestamp_for_a_file_candidate()
    {
        using var temporary = new TemporaryDirectory();
        var repository = temporary.GetPath("repo");
        Directory.CreateDirectory(repository);
        var candidate = Path.Combine(repository, "generated.bin");
        File.WriteAllText(candidate, "data");
        var expected = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);
        File.SetLastWriteTimeUtc(candidate, expected.UtcDateTime);

        var result = new FileTreeAnalyzer().Analyze(candidate, repository);

        Assert.True(result.IsSafe);
        Assert.Equal(1, result.FileCount);
        Assert.Equal(4, result.EstimatedBytes);
        Assert.Equal(expected, result.NewestWriteTimeUtc);
    }

    [Fact]
    public void Analyze_preserves_a_future_filesystem_timestamp_as_an_advisory_fact()
    {
        using var temporary = new TemporaryDirectory();
        var repository = temporary.GetPath("repo");
        var candidate = Path.Combine(repository, "obj");
        Directory.CreateDirectory(candidate);
        var artifact = Path.Combine(candidate, "artifact.bin");
        File.WriteAllText(artifact, "data");
        var future = new DateTimeOffset(2036, 1, 1, 0, 0, 0, TimeSpan.Zero);
        File.SetLastWriteTimeUtc(artifact, future.UtcDateTime);
        Directory.SetLastWriteTimeUtc(candidate, future.UtcDateTime);

        var result = new FileTreeAnalyzer().Analyze(candidate, repository);

        Assert.True(result.IsSafe);
        Assert.Equal(future, result.NewestWriteTimeUtc);
    }

    [Fact]
    public void Analyze_keeps_a_nested_mount_rejection_when_timestamps_are_unavailable()
    {
        using var temporary = new TemporaryDirectory();
        var candidate = temporary.GetPath("candidate");
        var nestedMount = Path.Combine(candidate, "nested-mount");
        Directory.CreateDirectory(nestedMount);
        File.WriteAllText(Path.Combine(nestedMount, "data.bin"), "data");
        var analyzer = new FileTreeAnalyzer(
            new TestIdentityProvider(nestedMount),
            new StubTimestampProvider(new Dictionary<string, DateTimeOffset>()));

        var result = analyzer.Analyze(candidate, temporary.Path);

        Assert.False(result.IsSafe);
        Assert.Null(result.NewestWriteTimeUtc);
        Assert.Contains(result.Warnings, warning =>
            warning.Path == nestedMount &&
            warning.Message.Contains("mount", StringComparison.OrdinalIgnoreCase));
    }

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
    [InlineData(0x1900u, true)]
    [InlineData(0x1100u, false)]
    [InlineData(0x0900u, false)]
    [InlineData(0x1800u, false)]
    [InlineData(0x0100u, false)]
    [InlineData(0x0800u, false)]
    [InlineData(0x1000u, false)]
    [InlineData(0u, false)]
    public void Linux_identity_requires_inode_birth_time_and_mount_id_masks(uint mask, bool expected)
    {
        Assert.Equal(expected, FileSystemIdentityProvider.HasRequiredLinuxIdentity(mask));
    }

    [Theory]
    [InlineData(0x1000u, true)]
    [InlineData(0x1800u, true)]
    [InlineData(0x0800u, false)]
    [InlineData(0u, false)]
    public void Linux_mount_identity_requires_mount_id_but_not_birth_time(uint mask, bool expected)
    {
        Assert.Equal(expected, FileSystemIdentityProvider.HasRequiredLinuxMountIdentity(mask));
    }

    [Fact]
    public void Native_mount_identity_survives_rename()
    {
        using var temporary = new TemporaryDirectory();
        var originalPath = temporary.GetPath("candidate");
        var movedPath = temporary.GetPath("moved-candidate");
        Directory.CreateDirectory(originalPath);
        var provider = new FileSystemIdentityProvider();
        Assert.True(provider.TryGetMountIdentity(originalPath, out var original, out var originalError), originalError);
        Assert.NotNull(original);

        Directory.Move(originalPath, movedPath);
        Assert.True(provider.TryGetMountIdentity(movedPath, out var moved, out var movedError), movedError);

        Assert.Equal(original, moved);
    }

    [Fact]
    public void Native_identity_birth_stamp_survives_rename_and_distinguishes_a_replacement()
    {
        using var temporary = new TemporaryDirectory();
        var originalPath = temporary.GetPath("candidate");
        var movedPath = temporary.GetPath("original-candidate");
        Directory.CreateDirectory(originalPath);
        var provider = new FileSystemIdentityProvider();
        Assert.True(provider.TryGetIdentity(originalPath, out var original, out var originalError), originalError);
        Assert.NotNull(original);
        Assert.NotEqual(default, original.BirthStamp);

        Directory.Move(originalPath, movedPath);
        Assert.True(provider.TryGetIdentity(movedPath, out var moved, out var movedError), movedError);
        Assert.NotNull(moved);
        Assert.True(CleanupIdentity.HasSameStableIdentity(original, moved));

        Thread.Sleep(TimeSpan.FromMilliseconds(25));
        Directory.CreateDirectory(originalPath);
        Assert.True(provider.TryGetIdentity(originalPath, out var replacement, out var replacementError), replacementError);
        Assert.NotNull(replacement);
        Assert.NotEqual(original.BirthStamp, replacement.BirthStamp);
        Assert.False(CleanupIdentity.HasSameStableIdentity(original, replacement));
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
            identity = new FileSystemIdentity(
                1,
                unchecked((ulong)System.IO.Path.GetFullPath(path).GetHashCode()),
                mountId,
                attributes,
                null,
                new FileSystemBirthStamp(0, 0));
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

    private sealed class StubTimestampProvider(
        IReadOnlyDictionary<string, DateTimeOffset> values) : IFileTimestampProvider
    {
        public bool TryGetLastWriteTimeUtc(string path, out DateTimeOffset value) =>
            values.TryGetValue(Path.GetFullPath(path), out value);
    }
}
