using RepoGlean.Cleaning;
using RepoGlean.Scanning;

namespace RepoGlean.Tests.Cleaning;

public sealed class CleanupIdentityTests
{
    [Fact]
    public void HasSameStableIdentity_rejects_a_reused_file_id_with_a_different_birth_stamp()
    {
        var captured = new FileSystemIdentity(
            1,
            2,
            "mount",
            FileAttributes.Directory,
            LinkTarget: null,
            new FileSystemBirthStamp(10, 20));
        var replacement = captured with
        {
            BirthStamp = new FileSystemBirthStamp(11, 20),
        };

        Assert.False(CleanupIdentity.HasSameStableIdentity(captured, replacement));
    }
}
