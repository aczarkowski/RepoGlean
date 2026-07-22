using DevCleaner.Scanning;
using DevCleaner.Tests.Support;

namespace DevCleaner.Tests.Scanning;

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

    private sealed class UnavailableIdentityProvider : IFileSystemIdentityProvider
    {
        public bool TryGetIdentity(string path, out FileSystemIdentity? identity, out string? error)
        {
            identity = null;
            error = "Stable filesystem identity is unavailable for this test.";
            return false;
        }

        public bool TryGetVolumeId(string path, out ulong volumeId, out string? error)
        {
            volumeId = 0;
            error = "Stable filesystem identity is unavailable for this test.";
            return false;
        }
    }
}
