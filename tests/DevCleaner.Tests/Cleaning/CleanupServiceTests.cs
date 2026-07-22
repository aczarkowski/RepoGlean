using DevCleaner.Cleaning;
using DevCleaner.Configuration;
using DevCleaner.Git;
using DevCleaner.Rules;
using DevCleaner.Scanning;
using DevCleaner.Tests.Support;

namespace DevCleaner.Tests.Cleaning;

public sealed class CleanupServiceTests
{
    [Fact]
    public async Task Execute_deletes_a_revalidated_ignored_candidate()
    {
        using var fixture = await CleanupFixture.CreateAsync();
        var candidate = await fixture.ScanSingleAsync();

        var result = await fixture.ExecuteAsync([candidate]);

        var item = Assert.Single(result.Items);
        Assert.Equal(CleanupOutcome.Deleted, item.Outcome);
        Assert.False(Directory.Exists(candidate.AbsolutePath));
        Assert.Equal(candidate.EstimatedBytes, result.EstimatedDeletedBytes);
    }

    [Fact]
    public async Task Dry_run_uses_the_same_validation_but_never_mutates_the_candidate()
    {
        using var fixture = await CleanupFixture.CreateAsync();
        var candidate = await fixture.ScanSingleAsync();

        var result = await fixture.ExecuteAsync([candidate], dryRun: true);

        var item = Assert.Single(result.Items);
        Assert.Equal(CleanupOutcome.Skipped, item.Outcome);
        Assert.Contains("dry run", item.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(candidate.AbsolutePath));
        Assert.Equal(0, result.EstimatedDeletedBytes);
    }

    [Fact]
    public async Task Revalidation_skips_content_that_became_tracked_after_the_scan()
    {
        using var fixture = await CleanupFixture.CreateAsync();
        var candidate = await fixture.ScanSingleAsync();
        await fixture.Repository.GitAsync("add", "-f", "obj/artifact.bin");

        var result = await fixture.ExecuteAsync([candidate]);

        AssertSkipped(result, "visible");
        Assert.True(Directory.Exists(candidate.AbsolutePath));
    }

    [Fact]
    public async Task Revalidation_skips_content_that_is_no_longer_ignored()
    {
        using var fixture = await CleanupFixture.CreateAsync();
        var candidate = await fixture.ScanSingleAsync();
        fixture.Repository.Write(".gitignore", "different/\n");

        var result = await fixture.ExecuteAsync([candidate]);

        AssertSkipped(result, "ignored");
        Assert.True(Directory.Exists(candidate.AbsolutePath));
    }

    [Fact]
    public async Task Revalidation_skips_a_path_replaced_after_the_scan()
    {
        using var fixture = await CleanupFixture.CreateAsync();
        var candidate = await fixture.ScanSingleAsync();
        Directory.Move(candidate.AbsolutePath, fixture.Repository.GetPath("old-obj"));
        fixture.Repository.WriteBytes("obj/replacement.bin", 7);

        var result = await fixture.ExecuteAsync([candidate]);

        AssertSkipped(result, "identity");
        Assert.True(File.Exists(fixture.Repository.GetPath("obj/replacement.bin")));
    }

    [Fact]
    public async Task Revalidation_skips_a_nested_repository_introduced_after_the_scan()
    {
        using var fixture = await CleanupFixture.CreateAsync();
        var candidate = await fixture.ScanSingleAsync();
        Directory.CreateDirectory(fixture.Repository.GetPath("obj/.git"));

        var result = await fixture.ExecuteAsync([candidate]);

        AssertSkipped(result, "repository");
        Assert.True(Directory.Exists(candidate.AbsolutePath));
    }

    [Fact]
    public async Task Revalidation_skips_a_candidate_replaced_by_a_symbolic_link()
    {
        using var fixture = await CleanupFixture.CreateAsync();
        var candidate = await fixture.ScanSingleAsync();
        var outside = fixture.Temporary.GetPath("outside");
        Directory.CreateDirectory(outside);
        Directory.Delete(candidate.AbsolutePath, recursive: true);
        Directory.CreateSymbolicLink(candidate.AbsolutePath, outside);

        var result = await fixture.ExecuteAsync([candidate]);

        AssertSkipped(result, "link");
        Assert.True(Directory.Exists(outside));
    }

    [Fact]
    public async Task Revalidation_skips_a_repository_ancestor_replaced_by_a_symbolic_link()
    {
        using var fixture = await CleanupFixture.CreateAsync();
        var candidate = await fixture.ScanSingleAsync();
        var relocatedRepository = fixture.Temporary.GetPath("relocated-repo");
        Directory.Move(fixture.Repository.Path, relocatedRepository);
        Directory.CreateSymbolicLink(fixture.Repository.Path, relocatedRepository);

        var result = await fixture.ExecuteAsync([candidate]);

        AssertSkipped(result, "link");
        Assert.True(Directory.Exists(Path.Combine(relocatedRepository, "obj")));
    }

    [Fact]
    public async Task Revalidation_rejects_a_candidate_outside_the_requested_roots()
    {
        using var fixture = await CleanupFixture.CreateAsync();
        var candidate = await fixture.ScanSingleAsync();
        var unrelatedRoot = fixture.Temporary.GetPath("unrelated");
        Directory.CreateDirectory(unrelatedRoot);

        var result = await fixture.ExecuteAsync([candidate], requestedRoots: [unrelatedRoot]);

        AssertSkipped(result, "requested root");
        Assert.True(Directory.Exists(candidate.AbsolutePath));
    }

    [Fact]
    public async Task Revalidation_requires_the_captured_rule_to_still_be_active_and_matching()
    {
        using var fixture = await CleanupFixture.CreateAsync();
        var candidate = await fixture.ScanSingleAsync();
        await fixture.Repository.GitAsync("rm", "--quiet", "project.csproj");

        var result = await fixture.ExecuteAsync([candidate]);

        AssertSkipped(result, "rule");
        Assert.True(Directory.Exists(candidate.AbsolutePath));
    }

    [Fact]
    public async Task A_deletion_failure_is_recorded_and_does_not_discard_other_results()
    {
        using var fixture = await CleanupFixture.CreateAsync(includeSecondCandidate: true);
        var candidates = await fixture.ScanAsync();
        var failingPath = candidates[0].AbsolutePath;
        var fileSystem = new InterceptingCleanupFileSystem(failingPath);

        var result = await fixture.ExecuteAsync(candidates, fileSystem: fileSystem);

        Assert.Equal(2, result.Items.Count);
        Assert.Contains(result.Items, item => item.Candidate.AbsolutePath == failingPath && item.Outcome == CleanupOutcome.Failed);
        Assert.Contains(result.Items, item => item.Candidate.AbsolutePath != failingPath && item.Outcome == CleanupOutcome.Deleted);
    }

    [Fact]
    public async Task Cancellation_after_a_completed_candidate_preserves_it_and_stops_scheduling()
    {
        using var fixture = await CleanupFixture.CreateAsync(includeSecondCandidate: true);
        var candidates = await fixture.ScanAsync();
        using var cancellation = new CancellationTokenSource();
        var fileSystem = new InterceptingCleanupFileSystem(cancelAfterDirectoryDelete: cancellation);

        var result = await fixture.ExecuteAsync(candidates, cancellationToken: cancellation.Token, fileSystem: fileSystem);

        Assert.True(result.IsInterrupted);
        var completed = Assert.Single(result.Items);
        Assert.Equal(CleanupOutcome.Deleted, completed.Outcome);
        Assert.False(Directory.Exists(completed.Candidate.AbsolutePath));
        Assert.True(Directory.Exists(candidates[1].AbsolutePath));
    }

    private static void AssertSkipped(CleanupResult result, string messageFragment)
    {
        var item = Assert.Single(result.Items);
        Assert.Equal(CleanupOutcome.Skipped, item.Outcome);
        Assert.Contains(messageFragment, item.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class CleanupFixture : IDisposable
    {
        private readonly GitClient git = new();

        private CleanupFixture(TemporaryDirectory temporary, GitTestRepository repository)
        {
            Temporary = temporary;
            Repository = repository;
        }

        public TemporaryDirectory Temporary { get; }

        public GitTestRepository Repository { get; }

        public static async Task<CleanupFixture> CreateAsync(bool includeSecondCandidate = false)
        {
            var temporary = new TemporaryDirectory();
            var repository = await GitTestRepository.CreateAsync(temporary.GetPath("repo"));
            repository.Write("project.csproj", "<Project />");
            repository.Write(".gitignore", "obj/\nsrc/obj/\n");
            repository.WriteBytes("obj/artifact.bin", 5);
            if (includeSecondCandidate) repository.WriteBytes("src/obj/artifact.bin", 7);
            await repository.CommitAllAsync();
            return new CleanupFixture(temporary, repository);
        }

        public async Task<IReadOnlyList<ArtifactCandidate>> ScanAsync()
        {
            var result = await new RepositoryScanner(git).ScanAsync(
                [Repository.Path],
                RuleCatalog.Create(DevCleanerConfig.Default));
            return result.Repositories.SelectMany(repository => repository.Candidates).OrderBy(candidate => candidate.AbsolutePath, StringComparer.Ordinal).ToArray();
        }

        public async Task<ArtifactCandidate> ScanSingleAsync() => Assert.Single(await ScanAsync());

        public Task<CleanupResult> ExecuteAsync(
            IReadOnlyList<ArtifactCandidate> candidates,
            bool dryRun = false,
            IReadOnlyList<string>? requestedRoots = null,
            CancellationToken cancellationToken = default,
            ICleanupFileSystem? fileSystem = null)
        {
            var service = new CleanupService(git, fileSystem: fileSystem);
            return service.ExecuteAsync(
                new CleanupRequest(candidates, requestedRoots ?? [Temporary.Path], RuleCatalog.Create(DevCleanerConfig.Default), dryRun),
                cancellationToken);
        }

        public void Dispose() => Temporary.Dispose();
    }

    private sealed class InterceptingCleanupFileSystem : ICleanupFileSystem
    {
        private readonly SystemCleanupFileSystem inner = new();
        private readonly string? failingPath;
        private readonly CancellationTokenSource? cancelAfterDirectoryDelete;

        public InterceptingCleanupFileSystem(string? failingPath = null, CancellationTokenSource? cancelAfterDirectoryDelete = null)
        {
            this.failingPath = failingPath;
            this.cancelAfterDirectoryDelete = cancelAfterDirectoryDelete;
        }

        public FileAttributes GetAttributes(string path) => inner.GetAttributes(path);

        public IReadOnlyList<string> GetFileSystemEntries(string path) => inner.GetFileSystemEntries(path);

        public void DeleteFile(string path) => inner.DeleteFile(path);

        public void DeleteDirectory(string path)
        {
            if (string.Equals(path, failingPath, StringComparison.Ordinal)) throw new IOException("Injected deletion failure.");
            inner.DeleteDirectory(path);
            cancelAfterDirectoryDelete?.Cancel();
        }
    }
}
