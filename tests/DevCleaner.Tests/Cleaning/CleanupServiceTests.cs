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
    public async Task Candidate_swap_at_quarantine_boundary_never_deletes_the_replacement_and_recovers_it()
    {
        using var fixture = await CleanupFixture.CreateAsync();
        var candidate = await fixture.ScanSingleAsync();
        var originalPath = fixture.Repository.GetPath("original-obj");
        var observer = new TestMutationObserver(beforeQuarantineMove: (_, _, _) =>
        {
            Directory.Move(candidate.AbsolutePath, originalPath);
            fixture.Repository.WriteBytes("obj/replacement.bin", 11);
        });

        var result = await fixture.ExecuteAsync([candidate], mutationObserver: observer);

        var failed = Assert.Single(result.Items);
        Assert.Equal(CleanupOutcome.Failed, failed.Outcome);
        Assert.Contains("identity", failed.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("restored", failed.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(originalPath, "artifact.bin")));
        Assert.True(File.Exists(fixture.Repository.GetPath("obj/replacement.bin")));
        Assert.Empty(Directory.GetDirectories(fixture.Temporary.Path, ".devcleaner-quarantine-*"));
    }

    [Fact]
    public async Task Ancestor_swap_at_quarantine_boundary_strands_the_mismatch_without_deleting_outside_targets()
    {
        using var fixture = await CleanupFixture.CreateAsync();
        var candidate = await fixture.ScanSingleAsync();
        var relocatedRepository = fixture.Temporary.GetPath("relocated-repo");
        var outside = fixture.Temporary.GetPath("outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "outside");
        Directory.CreateDirectory(Path.Combine(outside, "obj"));
        File.WriteAllText(Path.Combine(outside, "obj", "replacement.bin"), "replacement");
        var observer = new TestMutationObserver(beforeQuarantineMove: (_, _, _) =>
        {
            Directory.Move(fixture.Repository.Path, relocatedRepository);
            Directory.CreateSymbolicLink(fixture.Repository.Path, outside);
        });

        var result = await fixture.ExecuteAsync([candidate], mutationObserver: observer);

        var failed = Assert.Single(result.Items);
        Assert.Equal(CleanupOutcome.Failed, failed.Outcome);
        Assert.Contains("stranded", failed.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(relocatedRepository, "obj", "artifact.bin")));
        Assert.True(File.Exists(Path.Combine(outside, "keep.txt")));
        var quarantine = Assert.Single(Directory.GetDirectories(fixture.Temporary.Path, ".devcleaner-quarantine-*"));
        Assert.True(File.Exists(Path.Combine(quarantine, "payload", "replacement.bin")));
        Assert.Contains(quarantine, failed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Child_swap_before_recursive_delete_removes_the_link_as_a_leaf_and_preserves_its_target()
    {
        using var fixture = await CleanupFixture.CreateAsync();
        fixture.Repository.WriteBytes("obj/child/nested.bin", 3);
        var candidate = await fixture.ScanSingleAsync();
        var outsideChild = fixture.Temporary.GetPath("outside-child");
        var observer = new TestMutationObserver(beforeRecursiveDelete: (_, _, destinationPath) =>
        {
            Directory.Move(Path.Combine(destinationPath, "child"), outsideChild);
            Directory.CreateSymbolicLink(Path.Combine(destinationPath, "child"), outsideChild);
        });

        var result = await fixture.ExecuteAsync([candidate], mutationObserver: observer);

        var deleted = Assert.Single(result.Items);
        Assert.Equal(CleanupOutcome.Deleted, deleted.Outcome);
        Assert.True(File.Exists(Path.Combine(outsideChild, "nested.bin")));
        Assert.Empty(Directory.GetDirectories(fixture.Temporary.Path, ".devcleaner-quarantine-*"));
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

    [Fact]
    public async Task Mid_candidate_cancellation_records_the_partial_candidate_and_keeps_the_full_selected_count()
    {
        using var fixture = await CleanupFixture.CreateAsync(includeSecondCandidate: true, includeThirdCandidate: true);
        var candidates = await fixture.ScanAsync();
        using var cancellation = new CancellationTokenSource();
        var fileSystem = new InterceptingCleanupFileSystem(cancelAfterFileDeleteOnCall: 2, cancellation: cancellation);

        var result = await fixture.ExecuteAsync(candidates, cancellationToken: cancellation.Token, fileSystem: fileSystem);

        Assert.True(result.IsInterrupted);
        Assert.Equal(3, result.SelectedCount);
        Assert.Equal(2, result.Items.Count);
        Assert.Equal(CleanupOutcome.Deleted, result.Items[0].Outcome);
        Assert.Equal(CleanupOutcome.Failed, result.Items[1].Outcome);
        Assert.Contains("interrupt", result.Items[1].Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(candidates[0].AbsolutePath));
        Assert.True(Directory.Exists(candidates[1].AbsolutePath));
        Assert.True(Directory.Exists(candidates[2].AbsolutePath));
    }

    [Fact]
    public async Task Empty_quarantine_cleanup_failure_is_explicit_instead_of_hidden_as_success()
    {
        using var fixture = await CleanupFixture.CreateAsync();
        var candidate = await fixture.ScanSingleAsync();
        var fileSystem = new InterceptingCleanupFileSystem(failQuarantineCleanup: true);

        var result = await fixture.ExecuteAsync([candidate], fileSystem: fileSystem);

        var failed = Assert.Single(result.Items);
        Assert.Equal(CleanupOutcome.Failed, failed.Outcome);
        Assert.Contains("empty quarantine", failed.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(candidate.AbsolutePath));
        var quarantine = Assert.Single(Directory.GetDirectories(fixture.Temporary.Path, ".devcleaner-quarantine-*"));
        Assert.Empty(Directory.GetFileSystemEntries(quarantine));
        Assert.Contains(quarantine, failed.Message, StringComparison.Ordinal);
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

        public static async Task<CleanupFixture> CreateAsync(bool includeSecondCandidate = false, bool includeThirdCandidate = false)
        {
            var temporary = new TemporaryDirectory();
            var repository = await GitTestRepository.CreateAsync(temporary.GetPath("repo"));
            repository.Write("project.csproj", "<Project />");
            repository.Write(".gitignore", "obj/\nsrc/obj/\ntests/obj/\n");
            repository.WriteBytes("obj/artifact.bin", 5);
            if (includeSecondCandidate) repository.WriteBytes("src/obj/artifact.bin", 7);
            if (includeThirdCandidate) repository.WriteBytes("tests/obj/artifact.bin", 9);
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
            ICleanupFileSystem? fileSystem = null,
            ICleanupMutationObserver? mutationObserver = null)
        {
            var service = new CleanupService(git, fileSystem: fileSystem, mutationObserver: mutationObserver);
            return service.ExecuteAsync(
                new CleanupRequest(candidates, requestedRoots ?? [Temporary.Path], RuleCatalog.Create(DevCleanerConfig.Default), dryRun),
                cancellationToken);
        }

        public void Dispose() => Temporary.Dispose();
    }

    private sealed class TestMutationObserver(
        Action<ArtifactCandidate, string, string>? beforeQuarantineMove = null,
        Action<ArtifactCandidate, string, string>? beforeMovedIdentityCheck = null,
        Action<ArtifactCandidate, string, string>? beforeRecursiveDelete = null) : ICleanupMutationObserver
    {
        public void BeforeQuarantineMove(ArtifactCandidate candidate, string quarantineRoot, string destinationPath) =>
            beforeQuarantineMove?.Invoke(candidate, quarantineRoot, destinationPath);

        public void BeforeMovedIdentityCheck(ArtifactCandidate candidate, string quarantineRoot, string destinationPath) =>
            beforeMovedIdentityCheck?.Invoke(candidate, quarantineRoot, destinationPath);

        public void BeforeRecursiveDelete(ArtifactCandidate candidate, string quarantineRoot, string destinationPath) =>
            beforeRecursiveDelete?.Invoke(candidate, quarantineRoot, destinationPath);
    }

    private sealed class InterceptingCleanupFileSystem : ICleanupFileSystem
    {
        private readonly SystemCleanupFileSystem inner = new();
        private readonly string? failingPath;
        private readonly CancellationTokenSource? cancelAfterDirectoryDelete;
        private readonly int cancelAfterFileDeleteOnCall;
        private readonly CancellationTokenSource? cancellation;
        private readonly bool failQuarantineCleanup;
        private int ownedDeleteCalls;
        private bool deletionFailureInjected;

        public InterceptingCleanupFileSystem(
            string? failingPath = null,
            CancellationTokenSource? cancelAfterDirectoryDelete = null,
            int cancelAfterFileDeleteOnCall = 0,
            CancellationTokenSource? cancellation = null,
            bool failQuarantineCleanup = false)
        {
            this.failingPath = failingPath;
            this.cancelAfterDirectoryDelete = cancelAfterDirectoryDelete;
            this.cancelAfterFileDeleteOnCall = cancelAfterFileDeleteOnCall;
            this.cancellation = cancellation;
            this.failQuarantineCleanup = failQuarantineCleanup;
        }

        public FileAttributes GetAttributes(string path) => inner.GetAttributes(path);

        public void CreateDirectory(string path) => inner.CreateDirectory(path);

        public void Move(string sourcePath, string destinationPath, bool isDirectory) => inner.Move(sourcePath, destinationPath, isDirectory);

        public void DeleteOwnedObject(string path, bool isDirectory, CancellationToken cancellationToken)
        {
            ownedDeleteCalls++;
            if (failingPath is not null && !deletionFailureInjected)
            {
                deletionFailureInjected = true;
                throw new IOException("Injected deletion failure.");
            }

            if (ownedDeleteCalls == cancelAfterFileDeleteOnCall)
            {
                var file = Directory.GetFiles(path, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).First();
                File.Delete(file);
                cancellation?.Cancel();
                throw new OperationCanceledException(cancellationToken);
            }

            inner.DeleteOwnedObject(path, isDirectory, cancellationToken);
            cancelAfterDirectoryDelete?.Cancel();
        }

        public void DeleteDirectory(string path)
        {
            if (failQuarantineCleanup && Path.GetFileName(path).StartsWith(".devcleaner-quarantine-", StringComparison.Ordinal))
            {
                throw new IOException("Injected quarantine cleanup failure.");
            }
            if (string.Equals(path, failingPath, StringComparison.Ordinal)) throw new IOException("Injected deletion failure.");
            inner.DeleteDirectory(path);
            cancelAfterDirectoryDelete?.Cancel();
        }
    }
}
