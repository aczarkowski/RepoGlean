using DevCleaner.Git;
using DevCleaner.Scanning;
using DevCleaner.Tests.Support;

namespace DevCleaner.Tests.Scanning;

public sealed class RepositoryDiscoveryTests
{
    [Fact]
    public async Task DiscoverAsync_finds_git_directories_worktrees_and_submodules()
    {
        using var temporary = new TemporaryDirectory();
        var primary = await GitTestRepository.CreateAsync(temporary.GetPath("primary"));
        primary.Write("README.md", "primary");
        await primary.CommitAllAsync();

        var worktreePath = temporary.GetPath("worktree");
        await primary.GitAsync("worktree", "add", "--quiet", "-b", "test-worktree", worktreePath);

        var child = await GitTestRepository.CreateAsync(temporary.GetPath("child-source"));
        child.Write("README.md", "child");
        await child.CommitAllAsync();
        await primary.GitAsync("-c", "protocol.file.allow=always", "submodule", "add", "--quiet", child.Path, "modules/child");

        var result = await new RepositoryDiscovery(new GitClient()).DiscoverAsync([temporary.Path]);

        Assert.Contains(primary.Path, result.Repositories);
        Assert.Contains(worktreePath, result.Repositories);
        Assert.Contains(primary.GetPath("modules/child"), result.Repositories);
        Assert.DoesNotContain(result.Repositories, path => path.Contains($"{System.IO.Path.DirectorySeparatorChar}.git{System.IO.Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DiscoverAsync_finds_nested_repositories_and_honors_exclusions()
    {
        using var temporary = new TemporaryDirectory();
        var outer = await GitTestRepository.CreateAsync(temporary.GetPath("outer"));
        var nested = await GitTestRepository.CreateAsync(outer.GetPath("vendor/nested"));
        var excluded = await GitTestRepository.CreateAsync(temporary.GetPath("excluded/repo"));

        var result = await new RepositoryDiscovery(new GitClient()).DiscoverAsync(
            [temporary.Path],
            ["excluded"]);

        Assert.Contains(outer.Path, result.Repositories);
        Assert.Contains(nested.Path, result.Repositories);
        Assert.DoesNotContain(excluded.Path, result.Repositories);
    }

    [Fact]
    public async Task DiscoverAsync_does_not_follow_directory_links()
    {
        using var temporary = new TemporaryDirectory();
        var outside = await GitTestRepository.CreateAsync(temporary.GetPath("outside"));
        var scanRoot = temporary.GetPath("scan-root");
        Directory.CreateDirectory(scanRoot);
        var link = System.IO.Path.Combine(scanRoot, "linked-repository");
        try
        {
            Directory.CreateSymbolicLink(link, outside.Path);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
        {
            return;
        }

        var result = await new RepositoryDiscovery(new GitClient()).DiscoverAsync([scanRoot]);

        Assert.Empty(result.Repositories);
    }

    [Fact]
    public async Task DiscoverAsync_skips_implicit_platform_trees_unless_they_are_explicit_roots()
    {
        using var temporary = new TemporaryDirectory();
        var implicitRoot = temporary.GetPath("cache");
        var repository = await GitTestRepository.CreateAsync(System.IO.Path.Combine(implicitRoot, "repo"));
        var discovery = new RepositoryDiscovery(new GitClient(), [implicitRoot]);

        var skipped = await discovery.DiscoverAsync([temporary.Path]);
        var explicitResult = await discovery.DiscoverAsync([repository.Path]);

        Assert.DoesNotContain(repository.Path, skipped.Repositories);
        Assert.Contains(repository.Path, explicitResult.Repositories);
    }

    [Fact]
    public async Task DiscoverAsync_continues_after_an_inaccessible_directory_where_supported()
    {
        if (OperatingSystem.IsWindows()) return;
        using var temporary = new TemporaryDirectory();
        var visible = await GitTestRepository.CreateAsync(temporary.GetPath("visible"));
        var inaccessible = temporary.GetPath("inaccessible");
        Directory.CreateDirectory(inaccessible);
        File.SetUnixFileMode(inaccessible, UnixFileMode.None);
        try
        {
            var result = await new RepositoryDiscovery(new GitClient()).DiscoverAsync([temporary.Path]);

            Assert.Contains(visible.Path, result.Repositories);
            Assert.Contains(result.Warnings, warning => warning.Path == inaccessible);
        }
        finally
        {
            File.SetUnixFileMode(inaccessible, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public async Task DiscoverAsync_records_missing_or_inaccessible_paths_as_warnings()
    {
        using var temporary = new TemporaryDirectory();
        var missing = temporary.GetPath("missing");

        var result = await new RepositoryDiscovery(new GitClient()).DiscoverAsync([missing]);

        var warning = Assert.Single(result.Warnings);
        Assert.Equal(System.IO.Path.GetFullPath(missing), warning.Path);
    }

    [Fact]
    public async Task DiscoverAsync_observes_cancellation()
    {
        using var temporary = new TemporaryDirectory();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new RepositoryDiscovery(new GitClient()).DiscoverAsync([temporary.Path], cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task DiscoverAsync_treats_a_missing_git_executable_as_fatal()
    {
        using var temporary = new TemporaryDirectory();
        Directory.CreateDirectory(temporary.GetPath("repo/.git"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new RepositoryDiscovery(new GitClient("devcleaner-definitely-missing-git")).DiscoverAsync([temporary.Path]));

        Assert.Contains("Git executable", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
