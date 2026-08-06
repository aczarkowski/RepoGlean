using RepoGlean.Auditing;
using RepoGlean.Scanning;
using RepoGlean.Tests.Support;
using System.Diagnostics;

namespace RepoGlean.Tests.Auditing;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SecureAuditDescriptorCollection
{
    public const string Name = "Secure audit descriptor accounting";
}

[Collection(SecureAuditDescriptorCollection.Name)]
public sealed class SecureAuditFileSystemTests
{
    [Fact]
    public void Backend_enumeration_cancellation_is_prompt_discards_partial_results_and_does_not_leak_descriptors()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;
        using var temporary = new TemporaryDirectory();
        var root = temporary.GetPath("root");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "one.bin"), "one");
        File.WriteAllText(Path.Combine(root, "two.bin"), "two");
        var cancellation = new CancelOnDescendantTimestampProvider(root);
        var fileSystem = new SecureAuditFileSystem(timestampOverride: cancellation);
        Assert.True(fileSystem.TryOpenRoot(root, out var openedRoot, out var rootError), rootError);
        using (openedRoot)
        {
            Assert.NotNull(openedRoot);
            var descriptorsBefore = CountOpenDescriptors();
            var stopwatch = Stopwatch.StartNew();
            for (var iteration = 0; iteration < 32; iteration++)
            {
                using var source = new CancellationTokenSource();
                cancellation.Arm(source);
                IReadOnlyList<ISecureAuditEntry>? partial = null;

                Assert.Throws<OperationCanceledException>(() =>
                    openedRoot.TryEnumerate(source.Token, out partial, out _));

                Assert.NotNull(partial);
                Assert.Empty(partial);
            }

            stopwatch.Stop();
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"Cancellation took {stopwatch.Elapsed}.");
            var descriptorsAfter = CountOpenDescriptors();
            Assert.True(
                descriptorsAfter <= descriptorsBefore + 1,
                $"Secure enumeration leaked descriptors: before={descriptorsBefore}, after={descriptorsAfter}.");
            cancellation.Disarm();
            Assert.True(openedRoot.TryEnumerate(CancellationToken.None, out var finalEntries, out var finalError), finalError);
            try
            {
                Assert.Equal(2, finalEntries.Count);
            }
            finally
            {
                foreach (var entry in finalEntries) entry.Dispose();
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Repository_root_link_is_refused_with_or_without_a_trailing_separator(bool trailingSeparator)
    {
        if (OperatingSystem.IsWindows()) return;
        using var temporary = new TemporaryDirectory();
        var target = temporary.GetPath("target");
        Directory.CreateDirectory(target);
        var link = temporary.GetPath("root-link");
        Directory.CreateSymbolicLink(link, target);
        var requestedPath = trailingSeparator ? link + Path.DirectorySeparatorChar : link;

        var opened = new SecureAuditFileSystem().TryOpenRoot(requestedPath, out var root, out var error);

        root?.Dispose();
        Assert.False(opened);
        Assert.Null(root);
        Assert.Contains("securely open", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Linux_audit_metadata_contract_requires_inode_identity()
    {
        var withoutInode = UnixSecureAuditEntry.LinuxRequiredAuditMetadata & ~UnixSecureAuditEntry.LinuxStatxInode;

        Assert.False(UnixSecureAuditEntry.HasRequiredLinuxAuditMetadata(withoutInode));
        Assert.True(UnixSecureAuditEntry.HasRequiredLinuxAuditMetadata(UnixSecureAuditEntry.LinuxRequiredAuditMetadata));
    }

    [Fact]
    public void Windows_authoritative_identity_rejects_unavailable_and_zero_file_ids()
    {
        Assert.False(WindowsSecureAuditEntry.TryCreateAuthoritativeIdentity(
            0,
            Convert.FromHexString("01000000000000000000000000000000"),
            FileSystemEntryKind.RegularFile,
            out var unavailableVolume));
        Assert.Null(unavailableVolume);
        Assert.False(WindowsSecureAuditEntry.TryCreateAuthoritativeIdentity(
            17,
            [],
            FileSystemEntryKind.RegularFile,
            out var unavailable));
        Assert.Null(unavailable);
        Assert.False(WindowsSecureAuditEntry.TryCreateAuthoritativeIdentity(
            17,
            new byte[16],
            FileSystemEntryKind.RegularFile,
            out var zero));
        Assert.Null(zero);
    }

    [Fact]
    public void Windows_authoritative_identity_preserves_128_bit_file_ids_and_little_endian_layout()
    {
        var bytes = Convert.FromHexString("0102030405060708090A0B0C0D0E0F10");

        var created = WindowsSecureAuditEntry.TryCreateAuthoritativeIdentity(
            0xAABBCCDD,
            bytes,
            FileSystemEntryKind.Directory,
            out var identity);

        Assert.True(created);
        Assert.NotNull(identity);
        Assert.Equal(0x0807060504030201UL, identity.FileIdLow);
        Assert.Equal(0x100F0E0D0C0B0A09UL, identity.FileIdHigh);
    }

    [Fact]
    public void Windows_authoritative_identity_distinguishes_a_64_bit_collision_and_accepts_a_refs_like_high_half()
    {
        var first = Convert.FromHexString("88776655443322110100000000000000");
        var collision = Convert.FromHexString("88776655443322110200000000000000");
        var highOnly = Convert.FromHexString("00000000000000000100000000000000");

        Assert.True(WindowsSecureAuditEntry.TryCreateAuthoritativeIdentity(
            23, first, FileSystemEntryKind.RegularFile, out var firstIdentity));
        Assert.True(WindowsSecureAuditEntry.TryCreateAuthoritativeIdentity(
            23, collision, FileSystemEntryKind.RegularFile, out var collisionIdentity));
        Assert.True(WindowsSecureAuditEntry.TryCreateAuthoritativeIdentity(
            23, highOnly, FileSystemEntryKind.RegularFile, out var refsLikeIdentity));

        Assert.NotEqual(firstIdentity, collisionIdentity);
        Assert.NotNull(refsLikeIdentity);
        Assert.Equal(0UL, refsLikeIdentity.FileIdLow);
        Assert.Equal(1UL, refsLikeIdentity.FileIdHigh);
    }

    [Fact]
    public void Authoritative_identity_equality_includes_volume_file_id_and_kind()
    {
        var bytes = Convert.FromHexString("88776655443322111100FFEEDDCCBBAA");
        Assert.True(WindowsSecureAuditEntry.TryCreateAuthoritativeIdentity(
            23, bytes, FileSystemEntryKind.RegularFile, out var baseline));
        Assert.True(WindowsSecureAuditEntry.TryCreateAuthoritativeIdentity(
            23, bytes, FileSystemEntryKind.RegularFile, out var equal));
        Assert.True(WindowsSecureAuditEntry.TryCreateAuthoritativeIdentity(
            24, bytes, FileSystemEntryKind.RegularFile, out var otherVolume));
        Assert.True(WindowsSecureAuditEntry.TryCreateAuthoritativeIdentity(
            23, bytes, FileSystemEntryKind.Directory, out var otherKind));

        Assert.Equal(baseline, equal);
        Assert.NotEqual(baseline, otherVolume);
        Assert.NotEqual(baseline, otherKind);
    }

    [Fact]
    public void Current_platform_backend_enumerates_descriptor_metadata_without_following_links()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS() && !OperatingSystem.IsWindows()) return;
        using var temporary = new TemporaryDirectory();
        var root = temporary.GetPath("root");
        Directory.CreateDirectory(root);
        File.WriteAllBytes(Path.Combine(root, "regular.bin"), new byte[17]);
        Directory.CreateDirectory(Path.Combine(root, "directory"));
        File.WriteAllBytes(Path.Combine(root, "directory", "nested.bin"), new byte[19]);
        var external = temporary.GetPath("external");
        Directory.CreateDirectory(external);
        File.WriteAllBytes(Path.Combine(external, "outside.bin"), new byte[23]);
        var linkCreated = TryCreateDirectoryLink(Path.Combine(root, "link"), external);
        var fileSystem = new SecureAuditFileSystem();

        Assert.True(fileSystem.TryOpenRoot(root, out var openedRoot, out var rootError), rootError);
        using (openedRoot)
        {
            Assert.NotNull(openedRoot);
            Assert.Equal(FileSystemEntryKind.Directory, openedRoot.Kind);
            Assert.True(openedRoot.TryEnumerate(CancellationToken.None, out var entries, out var enumerationError), enumerationError);
            try
            {
                var regular = Assert.Single(entries, entry => entry.Name == "regular.bin");
                Assert.Equal(FileSystemEntryKind.RegularFile, regular.Kind);
                Assert.Equal(17, regular.Length);
                var directory = Assert.Single(entries, entry => entry.Name == "directory");
                Assert.Equal(FileSystemEntryKind.Directory, directory.Kind);
                Assert.True(directory.TryReopen(out var reopenedDirectory, out var reopenError), reopenError);
                using (reopenedDirectory)
                {
                    Assert.NotNull(reopenedDirectory);
                    Assert.True(reopenedDirectory.TryEnumerate(CancellationToken.None, out var nested, out var nestedError), nestedError);
                    try
                    {
                        Assert.Equal("nested.bin", Assert.Single(nested).Name);
                        Assert.DoesNotContain(nested, entry => entry.Name == "outside.bin");
                    }
                    finally
                    {
                        foreach (var entry in nested) entry.Dispose();
                    }
                }

                if (linkCreated)
                {
                    var link = Assert.Single(entries, entry => entry.Name == "link");
                    Assert.Equal(FileSystemEntryKind.Link, link.Kind);
                    Assert.False(link.TryEnumerate(CancellationToken.None, out _, out _));
                }

                Assert.DoesNotContain(entries, entry => entry.Name == "outside.bin");
            }
            finally
            {
                foreach (var entry in entries) entry.Dispose();
            }
        }
    }

    [Fact]
    public void Held_directory_handle_enumerates_the_validated_directory_after_its_path_becomes_an_external_link()
    {
        if (OperatingSystem.IsWindows()) return;
        using var temporary = new TemporaryDirectory();
        var root = temporary.GetPath("root");
        var target = Path.Combine(root, "target");
        var relocated = Path.Combine(root, "relocated");
        Directory.CreateDirectory(target);
        File.WriteAllBytes(Path.Combine(target, "local.bin"), new byte[17]);
        var external = temporary.GetPath("external");
        Directory.CreateDirectory(external);
        File.WriteAllBytes(Path.Combine(external, "outside.bin"), new byte[37]);
        var fileSystem = new SecureAuditFileSystem();
        Assert.True(fileSystem.TryOpenRoot(root, out var openedRoot, out var rootError), rootError);
        using (openedRoot)
        {
            Assert.NotNull(openedRoot);
            Assert.True(openedRoot.TryEnumerate(CancellationToken.None, out var entries, out var entriesError), entriesError);
            try
            {
                var snapshot = Assert.Single(entries, entry => entry.Name == "target");
                Assert.True(snapshot.TryReopen(out var validated, out var validationError), validationError);
                using (validated)
                {
                    Assert.NotNull(validated);
                    Directory.Move(target, relocated);
                    Directory.CreateSymbolicLink(target, external);

                    Assert.True(validated.TryEnumerate(CancellationToken.None, out var afterSwap, out var afterSwapError), afterSwapError);
                    try
                    {
                        Assert.Equal("local.bin", Assert.Single(afterSwap).Name);
                        Assert.DoesNotContain(afterSwap, entry => entry.Name == "outside.bin");
                    }
                    finally
                    {
                        foreach (var entry in afterSwap) entry.Dispose();
                    }

                    Assert.True(validated.TryReopen(out var current, out var currentError), currentError);
                    using (current)
                    {
                        Assert.NotNull(current);
                        Assert.Equal(FileSystemEntryKind.Link, current.Kind);
                    }
                }
            }
            finally
            {
                foreach (var entry in entries) entry.Dispose();
            }
        }
    }

    [Fact]
    public void Held_file_handle_retains_validated_metadata_after_its_path_becomes_an_external_link()
    {
        if (OperatingSystem.IsWindows()) return;
        using var temporary = new TemporaryDirectory();
        var root = temporary.GetPath("root");
        Directory.CreateDirectory(root);
        var target = Path.Combine(root, "target.bin");
        var relocated = Path.Combine(root, "relocated.bin");
        File.WriteAllBytes(target, new byte[17]);
        var external = temporary.GetPath("outside.bin");
        File.WriteAllBytes(external, new byte[37]);
        var fileSystem = new SecureAuditFileSystem();
        Assert.True(fileSystem.TryOpenRoot(root, out var openedRoot, out var rootError), rootError);
        using (openedRoot)
        {
            Assert.NotNull(openedRoot);
            Assert.True(openedRoot.TryEnumerate(CancellationToken.None, out var entries, out var entriesError), entriesError);
            try
            {
                var snapshot = Assert.Single(entries, entry => entry.Name == "target.bin");
                Assert.True(snapshot.TryReopen(out var validated, out var validationError), validationError);
                using (validated)
                {
                    Assert.NotNull(validated);
                    File.Move(target, relocated);
                    File.CreateSymbolicLink(target, external);

                    Assert.Equal(17, validated.Length);
                    Assert.NotEqual(new FileInfo(external).Length, validated.Length);
                    Assert.True(validated.TryReopen(out var current, out var currentError), currentError);
                    using (current)
                    {
                        Assert.NotNull(current);
                        Assert.Equal(FileSystemEntryKind.Link, current.Kind);
                    }
                }
            }
            finally
            {
                foreach (var entry in entries) entry.Dispose();
            }
        }
    }

    private static bool TryCreateDirectoryLink(string path, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(path, target);
            return true;
        }
        catch (Exception exception) when (exception is
            UnauthorizedAccessException or
            IOException or
            PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static int CountOpenDescriptors()
    {
        var path = OperatingSystem.IsMacOS() ? "/dev/fd" : "/proc/self/fd";
        return Directory.EnumerateFileSystemEntries(path).Count();
    }

    private sealed class CancelOnDescendantTimestampProvider(string root) : IFileTimestampProvider
    {
        private CancellationTokenSource? source;

        internal void Arm(CancellationTokenSource cancellation) => source = cancellation;

        internal void Disarm() => source = null;

        public bool TryGetLastWriteTimeUtc(string path, out DateTimeOffset value)
        {
            if (!string.Equals(path, root, StringComparison.Ordinal)) source?.Cancel();
            value = DateTimeOffset.UnixEpoch;
            return true;
        }
    }
}
