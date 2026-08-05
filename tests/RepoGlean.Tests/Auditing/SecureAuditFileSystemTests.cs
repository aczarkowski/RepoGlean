using RepoGlean.Auditing;
using RepoGlean.Scanning;
using RepoGlean.Tests.Support;

namespace RepoGlean.Tests.Auditing;

public sealed class SecureAuditFileSystemTests
{
    [Fact]
    public void Linux_audit_metadata_contract_requires_inode_identity()
    {
        var withoutInode = UnixSecureAuditEntry.LinuxRequiredAuditMetadata & ~UnixSecureAuditEntry.LinuxStatxInode;

        Assert.False(UnixSecureAuditEntry.HasRequiredLinuxAuditMetadata(withoutInode));
        Assert.True(UnixSecureAuditEntry.HasRequiredLinuxAuditMetadata(UnixSecureAuditEntry.LinuxRequiredAuditMetadata));
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
            Assert.True(openedRoot.TryEnumerate(out var entries, out var enumerationError), enumerationError);
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
                    Assert.True(reopenedDirectory.TryEnumerate(out var nested, out var nestedError), nestedError);
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
                    Assert.False(link.TryEnumerate(out _, out _));
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
            Assert.True(openedRoot.TryEnumerate(out var entries, out var entriesError), entriesError);
            try
            {
                var snapshot = Assert.Single(entries, entry => entry.Name == "target");
                Assert.True(snapshot.TryReopen(out var validated, out var validationError), validationError);
                using (validated)
                {
                    Assert.NotNull(validated);
                    Directory.Move(target, relocated);
                    Directory.CreateSymbolicLink(target, external);

                    Assert.True(validated.TryEnumerate(out var afterSwap, out var afterSwapError), afterSwapError);
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
            Assert.True(openedRoot.TryEnumerate(out var entries, out var entriesError), entriesError);
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
}
