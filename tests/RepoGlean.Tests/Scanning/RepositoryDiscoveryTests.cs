using RepoGlean.Git;
using RepoGlean.Progress;
using RepoGlean.Scanning;
using RepoGlean.Tests.Support;

namespace RepoGlean.Tests.Scanning;

public sealed class RepositoryDiscoveryTests
{
    [Fact]
    public async Task DiscoverAsync_reports_repository_discovery_without_directory_traversal_events()
    {
        using var temporary = new TemporaryDirectory();
        _ = await GitTestRepository.CreateAsync(temporary.GetPath("first"));
        _ = await GitTestRepository.CreateAsync(temporary.GetPath("second"));
        Directory.CreateDirectory(temporary.GetPath("ordinary-directory/nested"));
        var progress = new RecordingProgress();
        var discovery = new RepositoryDiscovery(new GitClient(), progress, ProgressOperation.Clean);

        var result = await discovery.DiscoverAsync([temporary.Path]);

        Assert.Equal(2, result.Repositories.Count);
        var events = progress.Events;
        Assert.Equal(ProgressEventKind.DiscoveryStarted, events.First().Kind);
        Assert.Equal(2, events.Count(item => item.Kind == ProgressEventKind.RepositoryFound));
        Assert.Equal(
            [1L, 2L],
            events
                .Where(item => item.Kind == ProgressEventKind.RepositoryFound)
                .Select(item => item.RepositoryCount));
        Assert.Equal(ProgressEventKind.DiscoveryCompleted, events.Last().Kind);
        Assert.Equal(2, events.Last().RepositoryCount);
        Assert.All(events, item => Assert.Equal(ProgressOperation.Clean, item.Operation));
        Assert.DoesNotContain(events, item =>
            item.Path is not null &&
            item.Path.Contains("ordinary-directory", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DiscoverAsync_preserves_results_and_warnings_when_progress_reporting_fails()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await GitTestRepository.CreateAsync(temporary.GetPath("repository"));
        var missing = temporary.GetPath("missing");
        var progress = new ThrowingProgress(new InvalidOperationException("injected progress failure"));
        var discovery = new RepositoryDiscovery(new GitClient(), progress, ProgressOperation.Scan);

        var result = await discovery.DiscoverAsync([repository.Path, missing]);

        Assert.Equal([repository.Path], result.Repositories);
        var warning = Assert.Single(result.Warnings);
        Assert.Equal(missing, warning.Path);
        Assert.Equal("Scan root does not exist or is not an accessible directory.", warning.Message);
    }

    [Fact]
    public async Task DiscoverAsync_does_not_swallow_catastrophic_progress_failures()
    {
        using var temporary = new TemporaryDirectory();
        var failure = new OutOfMemoryException("injected catastrophic progress failure");
        var progress = new ThrowingProgress(failure);
        var discovery = new RepositoryDiscovery(new GitClient(), progress, ProgressOperation.Scan);

        var exception = await Assert.ThrowsAsync<OutOfMemoryException>(() =>
            discovery.DiscoverAsync([temporary.Path]));

        Assert.Same(failure, exception);
    }

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
        var progress = new RecordingProgress();

        var result = await new RepositoryDiscovery(new GitClient(), progress, ProgressOperation.Scan)
            .DiscoverAsync([missing]);

        var warning = Assert.Single(result.Warnings);
        Assert.Equal(System.IO.Path.GetFullPath(missing), warning.Path);
        var warningEvent = Assert.Single(progress.Events, item => item.Kind == ProgressEventKind.Warning);
        Assert.Equal(warning.Path, warningEvent.Path);
        Assert.Equal(warning.Message, warningEvent.Message);
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

        var exception = await Assert.ThrowsAsync<GitUnavailableException>(() =>
            new RepositoryDiscovery(new GitClient("repoglean-definitely-missing-git")).DiscoverAsync([temporary.Path]));

        Assert.Contains("Git executable", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DiscoverAsync_warns_and_continues_when_a_git_marker_is_broken()
    {
        using var temporary = new TemporaryDirectory();
        var valid = await GitTestRepository.CreateAsync(temporary.GetPath("valid"));
        var broken = temporary.GetPath("broken");
        Directory.CreateDirectory(broken);
        File.WriteAllText(System.IO.Path.Combine(broken, ".git"), "gitdir: missing-git-directory\n");

        var result = await new RepositoryDiscovery(new GitClient()).DiscoverAsync([temporary.Path]);

        Assert.Contains(valid.Path, result.Repositories);
        Assert.Contains(result.Warnings, warning =>
            warning.Path == broken &&
            warning.Message.Contains("rev-parse", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DiscoverAsync_does_not_cross_a_volume_boundary_below_a_root()
    {
        using var temporary = new TemporaryDirectory();
        var sameVolume = await GitTestRepository.CreateAsync(temporary.GetPath("same-volume"));
        var mounted = await GitTestRepository.CreateAsync(temporary.GetPath("mounted/foreign"));
        var boundary = new TestVolumeBoundary(temporary.GetPath("mounted"));
        var discovery = new RepositoryDiscovery(new GitClient(), [], boundary, new TestDriveRootProvider(temporary.Path));

        var result = await discovery.DiscoverAsync([], allDrives: true);

        Assert.Contains(sameVolume.Path, result.Repositories);
        Assert.DoesNotContain(mounted.Path, result.Repositories);
        Assert.Contains(result.Warnings, warning =>
            warning.Path == temporary.GetPath("mounted") &&
            warning.Message.Contains("volume", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DiscoverAsync_honors_a_foreign_volume_when_it_is_the_explicit_root()
    {
        using var temporary = new TemporaryDirectory();
        var mountedRoot = temporary.GetPath("mounted");
        var repository = await GitTestRepository.CreateAsync(System.IO.Path.Combine(mountedRoot, "repo"));
        var discovery = new RepositoryDiscovery(new GitClient(), [], new TestVolumeBoundary(mountedRoot));

        var result = await discovery.DiscoverAsync([mountedRoot]);

        Assert.Contains(repository.Path, result.Repositories);
    }

    [Fact]
    public async Task DiscoverAsync_retains_fixed_drive_enumeration_warnings()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await GitTestRepository.CreateAsync(temporary.GetPath("repo"));
        var driveWarning = new OperationWarning("unreadable-drive", "Unable to inspect fixed drive.");
        var discovery = new RepositoryDiscovery(
            new GitClient(),
            [],
            new TestVolumeBoundary(temporary.GetPath("foreign")),
            new TestDriveRootProvider(temporary.Path, driveWarning));

        var result = await discovery.DiscoverAsync([], allDrives: true);

        Assert.Contains(repository.Path, result.Repositories);
        Assert.Contains(driveWarning, result.Warnings);
    }

    private sealed class TestVolumeBoundary(string foreignRoot) : IVolumeBoundary
    {
        public bool TryGetMountIdentity(string path, out FileSystemMountIdentity? identity, out string? error)
        {
            var isForeign = RepositoryDiscovery.IsSameOrDescendant(System.IO.Path.GetFullPath(path), System.IO.Path.GetFullPath(foreignRoot));
            identity = new FileSystemMountIdentity(isForeign ? 2UL : 1UL, isForeign ? "foreign" : "root");
            error = null;
            return true;
        }
    }

    private sealed class TestDriveRootProvider(string root, OperationWarning? warning = null) : IDriveRootProvider
    {
        public DriveRootDiscoveryResult GetFixedDriveRoots() => new([root], warning is null ? [] : [warning]);
    }
}

internal sealed class ThrowingProgress(Exception failure) : IOperationProgress
{
    public void Report(OperationProgressEvent progressEvent) => throw failure;

    public void Pause()
    {
    }

    public void Resume()
    {
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
