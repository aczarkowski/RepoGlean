using DevCleaner.Cleaning;
using DevCleaner.Cli;
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
    public async Task Candidate_swap_at_quarantine_boundary_never_moves_or_deletes_either_object()
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
        Assert.Contains("not moved or deleted", failed.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(originalPath, "artifact.bin")));
        Assert.True(File.Exists(fixture.Repository.GetPath("obj/replacement.bin")));
        Assert.Empty(Directory.GetDirectories(fixture.Repository.Path, ".devcleaner-quarantine-*"));
    }

    [Fact]
    public async Task Git_add_force_at_quarantine_boundary_revokes_cleanup_authority()
    {
        using var fixture = await CleanupFixture.CreateAsync();
        var candidate = await fixture.ScanSingleAsync();
        var observer = new TestMutationObserver(beforeQuarantineMove: (_, _, _) =>
            fixture.Repository.GitAsync("add", "-f", "obj/artifact.bin").GetAwaiter().GetResult());

        var result = await fixture.ExecuteAsync([candidate], mutationObserver: observer);

        var failed = Assert.Single(result.Items);
        Assert.Equal(CleanupOutcome.Failed, failed.Outcome);
        Assert.Contains("tracked", failed.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(candidate.AbsolutePath, "artifact.bin")));
    }

    [Fact]
    public async Task Ignore_change_at_quarantine_boundary_revokes_cleanup_authority()
    {
        using var fixture = await CleanupFixture.CreateAsync();
        var candidate = await fixture.ScanSingleAsync();
        var observer = new TestMutationObserver(beforeQuarantineMove: (_, _, _) =>
            fixture.Repository.Write(".gitignore", "different/\n"));

        var result = await fixture.ExecuteAsync([candidate], mutationObserver: observer);

        var failed = Assert.Single(result.Items);
        Assert.Equal(CleanupOutcome.Failed, failed.Outcome);
        Assert.Contains("ignored", failed.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(candidate.AbsolutePath, "artifact.bin")));
    }

    [Fact]
    public async Task Repository_root_replacement_at_quarantine_boundary_revokes_cleanup_authority()
    {
        using var fixture = await CleanupFixture.CreateAsync();
        var candidate = await fixture.ScanSingleAsync();
        var relocatedRepository = fixture.Temporary.GetPath("relocated-repository-root");
        var observer = new TestMutationObserver(beforeQuarantineMove: (_, _, _) =>
        {
            Directory.Move(fixture.Repository.Path, relocatedRepository);
            Directory.CreateDirectory(fixture.Repository.Path);
            foreach (var entry in Directory.GetFileSystemEntries(relocatedRepository))
            {
                var destination = Path.Combine(fixture.Repository.Path, Path.GetFileName(entry));
                if (Directory.Exists(entry)) Directory.Move(entry, destination);
                else File.Move(entry, destination);
            }
        });

        var result = await fixture.ExecuteAsync([candidate], mutationObserver: observer);

        var failed = Assert.Single(result.Items);
        Assert.Equal(CleanupOutcome.Failed, failed.Outcome);
        Assert.Contains("repository", failed.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("identity", failed.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(candidate.AbsolutePath, "artifact.bin")));
    }

    [Fact]
    public async Task Tracked_index_entry_created_while_original_path_is_absent_revokes_cleanup_authority()
    {
        using var fixture = await CleanupFixture.CreateAsync();
        var candidate = await fixture.ScanSingleAsync();
        var observer = new TestMutationObserver(beforeMovedIdentityCheck: (_, _, destinationPath) =>
        {
            var blob = fixture.Repository.GitAsync("hash-object", "-w", Path.Combine(destinationPath, "artifact.bin"))
                .GetAwaiter().GetResult().Trim();
            fixture.Repository.GitAsync("update-index", "--add", "--cacheinfo", $"100644,{blob},obj/artifact.bin")
                .GetAwaiter().GetResult();
        });

        var result = await fixture.ExecuteAsync([candidate], mutationObserver: observer);

        var failed = Assert.Single(result.Items);
        Assert.Equal(CleanupOutcome.Failed, failed.Outcome);
        Assert.Contains("tracked", failed.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(candidate.AbsolutePath, "artifact.bin")));
        Assert.Equal("obj/artifact.bin", (await fixture.Repository.GitAsync("ls-files", "obj/artifact.bin")).Trim());
    }

    [Fact]
    public async Task Git_becoming_unavailable_after_ownership_recovers_and_reports_the_candidate()
    {
        if (OperatingSystem.IsWindows()) return;

        using var fixture = await CleanupFixture.CreateAsync();
        var candidate = await fixture.ScanSingleAsync();
        var wrapper = fixture.Temporary.GetPath("git-disappears-after-ownership");
        File.WriteAllText(wrapper, "#!/bin/sh\nexec git \"$@\"\n");
        File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var observer = new TestMutationObserver(beforeMovedIdentityCheck: (_, _, _) => File.Delete(wrapper));

        var result = await fixture.ExecuteAsync(
            [candidate],
            mutationObserver: observer,
            cleanupGit: new GitClient(wrapper));

        var failed = Assert.Single(result.Items);
        Assert.Equal(CleanupOutcome.Failed, failed.Outcome);
        Assert.Contains("Git", failed.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("restored", failed.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(candidate.AbsolutePath, "artifact.bin")));
    }

    [Fact]
    public async Task Rule_marker_change_after_ownership_revokes_cleanup_authority()
    {
        using var fixture = await CleanupFixture.CreateAsync();
        fixture.Repository.Write("obj/quarantined-marker.csproj", "<Project />");
        var candidate = await fixture.ScanSingleAsync();
        var observer = new TestMutationObserver(beforeMovedIdentityCheck: (_, _, _) =>
            fixture.Repository.GitAsync("rm", "--quiet", "project.csproj").GetAwaiter().GetResult());

        var result = await fixture.ExecuteAsync([candidate], mutationObserver: observer);

        var failed = Assert.Single(result.Items);
        Assert.Equal(CleanupOutcome.Failed, failed.Outcome);
        Assert.Contains("rule", failed.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(candidate.AbsolutePath, "artifact.bin")));
    }

    [Fact]
    public async Task Prior_stranded_quarantine_cannot_keep_a_later_candidate_rule_active()
    {
        using var fixture = await CleanupFixture.CreateAsync(includeSecondCandidate: true);
        fixture.Repository.Write("obj/quarantined-marker.csproj", "<Project />");
        var candidates = await fixture.ScanAsync();
        var firstCandidate = Assert.Single(candidates, candidate => candidate.RelativePath == "obj");
        var laterCandidate = Assert.Single(candidates, candidate => candidate.RelativePath == "src/obj");
        var observer = new TestMutationObserver(beforeMovedIdentityCheck: (candidate, _, _) =>
        {
            if (candidate != firstCandidate) return;
            fixture.Repository.GitAsync("rm", "--quiet", "project.csproj").GetAwaiter().GetResult();
            Directory.CreateDirectory(candidate.AbsolutePath);
        });

        var result = await fixture.ExecuteAsync(candidates, mutationObserver: observer);

        Assert.Equal(2, result.Items.Count);
        Assert.Equal(CleanupOutcome.Failed, result.Items[0].Outcome);
        Assert.Equal(CleanupOutcome.Skipped, result.Items[1].Outcome);
        Assert.Contains("rule", result.Items[1].Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(laterCandidate.AbsolutePath, "artifact.bin")));
        var quarantine = Assert.Single(Directory.GetDirectories(fixture.Repository.Path, ".devcleaner-quarantine-*"));
        Assert.True(File.Exists(Path.Combine(quarantine, "payload", "quarantined-marker.csproj")));
    }

    [Fact]
    public async Task Ignore_change_at_final_deletion_boundary_revokes_cleanup_authority()
    {
        using var fixture = await CleanupFixture.CreateAsync();
        var candidate = await fixture.ScanSingleAsync();
        var observer = new TestMutationObserver(beforeRecursiveDelete: (_, _, _) =>
            fixture.Repository.Write(".gitignore", "different/\n"));

        var result = await fixture.ExecuteAsync([candidate], mutationObserver: observer);

        var failed = Assert.Single(result.Items);
        Assert.Equal(CleanupOutcome.Failed, failed.Outcome);
        Assert.Contains("ignored", failed.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(candidate.AbsolutePath, "artifact.bin")));
    }

    [Fact]
    public async Task Tracked_quarantine_payload_at_final_boundary_revokes_cleanup_authority()
    {
        using var fixture = await CleanupFixture.CreateAsync();
        var candidate = await fixture.ScanSingleAsync();
        var observer = new TestMutationObserver(beforeRecursiveDelete: (_, _, destinationPath) =>
        {
            var relativePayload = Path.GetRelativePath(fixture.Repository.Path, destinationPath).Replace('\\', '/');
            fixture.Repository.GitAsync("add", "-f", "--", $"{relativePayload}/artifact.bin")
                .GetAwaiter().GetResult();
        });

        var result = await fixture.ExecuteAsync([candidate], mutationObserver: observer);

        var failed = Assert.Single(result.Items);
        Assert.Equal(CleanupOutcome.Failed, failed.Outcome);
        Assert.Contains("tracked", failed.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(candidate.AbsolutePath, "artifact.bin")));
    }

    [Fact]
    public async Task Ancestor_swap_at_quarantine_boundary_rejects_the_mismatch_without_moving_outside_targets()
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
        Assert.Contains("not moved or deleted", failed.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(relocatedRepository, "obj", "artifact.bin")));
        Assert.True(File.Exists(Path.Combine(outside, "keep.txt")));
        Assert.True(File.Exists(Path.Combine(outside, "obj", "replacement.bin")));
        Assert.Empty(Directory.GetDirectories(fixture.Repository.Path, ".devcleaner-quarantine-*"));
    }

    [Fact]
    public async Task File_ancestor_swap_after_validation_never_invokes_the_mover_or_mutates_the_outside_file()
    {
        using var fixture = await CleanupFixture.CreateFileCandidateAsync();
        var candidate = await fixture.ScanSingleAsync();
        var relocatedRepository = fixture.Temporary.GetPath("relocated-file-repo");
        var outside = fixture.Temporary.GetPath("outside-file-root");
        Directory.CreateDirectory(outside);
        var outsidePath = Path.Combine(outside, "artifact.cache");
        var outsideBytes = new byte[] { 91, 17, 203, 4, 88 };
        File.WriteAllBytes(outsidePath, outsideBytes);
        var mover = new RecordingAtomicFileMover();
        var observer = new TestMutationObserver(beforeQuarantineMove: (_, _, _) =>
        {
            Directory.Move(fixture.Repository.Path, relocatedRepository);
            Directory.CreateSymbolicLink(fixture.Repository.Path, outside);
        });

        var result = await fixture.ExecuteAsync([candidate], mutationObserver: observer, atomicFileMover: mover);

        var failed = Assert.Single(result.Items);
        Assert.Equal(CleanupOutcome.Failed, failed.Outcome);
        Assert.Contains("identity", failed.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, mover.MoveCalls);
        Assert.Equal(outsideBytes, File.ReadAllBytes(outsidePath));
        Assert.True(File.Exists(Path.Combine(relocatedRepository, "artifact.cache")));
        Assert.Empty(Directory.GetDirectories(fixture.Repository.Path, ".devcleaner-quarantine-*"));
    }

    [Fact]
    public void Native_atomic_mover_never_overwrites_an_existing_destination()
    {
        using var temporary = new TemporaryDirectory();
        var source = temporary.GetPath("source.bin");
        var destination = temporary.GetPath("destination.bin");
        File.WriteAllText(source, "source");
        File.WriteAllText(destination, "destination");

        var exception = Assert.Throws<IOException>(() => new NativeAtomicFileMover().MoveNoCopy(source, destination));

        Assert.Contains("atomic", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("source", File.ReadAllText(source));
        Assert.Equal("destination", File.ReadAllText(destination));
    }

    [Fact]
    public async Task Atomic_mover_failure_leaves_the_source_and_reports_failure()
    {
        using var fixture = await CleanupFixture.CreateAsync();
        var candidate = await fixture.ScanSingleAsync();
        var mover = new FailingAtomicFileMover();

        var result = await fixture.ExecuteAsync([candidate], atomicFileMover: mover);

        var failed = Assert.Single(result.Items);
        Assert.Equal(CleanupOutcome.Failed, failed.Outcome);
        Assert.Contains("Injected atomic move failure", failed.Message, StringComparison.Ordinal);
        Assert.Equal(1, mover.MoveCalls);
        Assert.True(Directory.Exists(candidate.AbsolutePath));
        Assert.True(File.Exists(Path.Combine(candidate.AbsolutePath, "artifact.bin")));
        Assert.Empty(Directory.GetDirectories(fixture.Repository.Path, ".devcleaner-quarantine-*"));
    }

    [Fact]
    public async Task Recovery_inspection_failure_reports_the_exact_possible_payload_path()
    {
        using var fixture = await CleanupFixture.CreateAsync();
        var candidate = await fixture.ScanSingleAsync();
        var failInspection = false;
        var fileSystem = new InterceptingCleanupFileSystem(
            failGetAttributes: path => failInspection && string.Equals(path, fixture.Temporary.Path, StringComparison.Ordinal));
        string? destination = null;
        var observer = new TestMutationObserver(beforeMovedIdentityCheck: (_, _, destinationPath) =>
        {
            destination = destinationPath;
            Directory.CreateDirectory(Path.Combine(destinationPath, ".git"));
            failInspection = true;
        });

        var result = await fixture.ExecuteAsync([candidate], fileSystem: fileSystem, mutationObserver: observer);

        var failed = Assert.Single(result.Items);
        Assert.Equal(CleanupOutcome.Failed, failed.Outcome);
        Assert.NotNull(destination);
        Assert.Contains("Injected recovery inspection failure", failed.Message, StringComparison.Ordinal);
        Assert.Contains(destination, failed.Message, StringComparison.Ordinal);
        Assert.True(Directory.Exists(destination));
    }

    [Theory]
    [InlineData(EarlyQuarantineFailure.IdentityUnavailable)]
    [InlineData(EarlyQuarantineFailure.MountMismatch)]
    [InlineData(EarlyQuarantineFailure.AtomicMove)]
    public async Task Early_failure_never_discards_empty_quarantine_cleanup_failure(EarlyQuarantineFailure failure)
    {
        using var fixture = await CleanupFixture.CreateAsync();
        var candidate = await fixture.ScanSingleAsync();
        var fileSystem = new InterceptingCleanupFileSystem(failQuarantineCleanup: true);
        var identityProvider = failure is EarlyQuarantineFailure.IdentityUnavailable or EarlyQuarantineFailure.MountMismatch
            ? new InterceptingIdentityProvider(failure)
            : null;
        var mover = failure == EarlyQuarantineFailure.AtomicMove ? new FailingAtomicFileMover() : null;

        var result = await fixture.ExecuteAsync(
            [candidate],
            fileSystem: fileSystem,
            identityProvider: identityProvider,
            atomicFileMover: mover);

        var failed = Assert.Single(result.Items);
        Assert.Equal(CleanupOutcome.Failed, failed.Outcome);
        Assert.Contains("Injected quarantine cleanup failure", failed.Message, StringComparison.Ordinal);
        var quarantine = Assert.Single(Directory.GetDirectories(fixture.Repository.Path, ".devcleaner-quarantine-*"));
        Assert.Contains(quarantine, failed.Message, StringComparison.Ordinal);
        Assert.True(Directory.Exists(candidate.AbsolutePath));
    }

    [Fact]
    public async Task Pre_move_cancellation_records_empty_quarantine_cleanup_failure_and_path()
    {
        using var fixture = await CleanupFixture.CreateAsync();
        var candidate = await fixture.ScanSingleAsync();
        using var cancellation = new CancellationTokenSource();
        var fileSystem = new InterceptingCleanupFileSystem(failQuarantineCleanup: true);
        var observer = new TestMutationObserver(beforeQuarantineMove: (_, _, _) => cancellation.Cancel());

        var result = await fixture.ExecuteAsync(
            [candidate],
            cancellationToken: cancellation.Token,
            fileSystem: fileSystem,
            mutationObserver: observer);

        Assert.True(result.IsInterrupted);
        var failed = Assert.Single(result.Items);
        Assert.Equal(CleanupOutcome.Failed, failed.Outcome);
        Assert.Contains("interrupt", failed.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Injected quarantine cleanup failure", failed.Message, StringComparison.Ordinal);
        var quarantine = Assert.Single(Directory.GetDirectories(fixture.Repository.Path, ".devcleaner-quarantine-*"));
        Assert.Contains(quarantine, failed.Message, StringComparison.Ordinal);
        Assert.True(Directory.Exists(candidate.AbsolutePath));
    }

    [Fact]
    public async Task Child_swap_before_recursive_delete_stops_cleanup_and_preserves_both_trees()
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

        var failed = Assert.Single(result.Items);
        Assert.Equal(CleanupOutcome.Failed, failed.Outcome);
        Assert.Contains("snapshot", failed.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(outsideChild, "nested.bin")));
        Assert.True(Directory.Exists(Path.Combine(candidate.AbsolutePath, "child")));
    }

    [Fact]
    public async Task Stable_descendant_link_is_deleted_as_a_leaf_and_its_target_survives()
    {
        using var fixture = await CleanupFixture.CreateAsync();
        var candidate = await fixture.ScanSingleAsync();
        var outside = fixture.Temporary.GetPath("outside-link-target");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "keep.bin"), "keep");
        var observer = new TestMutationObserver(beforeMovedIdentityCheck: (_, _, destinationPath) =>
            Directory.CreateSymbolicLink(Path.Combine(destinationPath, "stable-link"), outside));

        var result = await fixture.ExecuteAsync([candidate], mutationObserver: observer);

        var deleted = Assert.Single(result.Items);
        Assert.Equal(CleanupOutcome.Deleted, deleted.Outcome);
        Assert.True(File.Exists(Path.Combine(outside, "keep.bin")));
        Assert.False(Directory.Exists(candidate.AbsolutePath));
    }

    [Fact]
    public async Task Descendant_directory_replacement_at_final_boundary_is_detected_before_deletion()
    {
        using var fixture = await CleanupFixture.CreateAsync();
        fixture.Repository.WriteBytes("obj/child/original.bin", 3);
        var candidate = await fixture.ScanSingleAsync();
        var originalChild = fixture.Temporary.GetPath("original-child");
        var observer = new TestMutationObserver(beforeRecursiveDelete: (_, _, destinationPath) =>
        {
            Directory.Move(Path.Combine(destinationPath, "child"), originalChild);
            Directory.CreateDirectory(Path.Combine(destinationPath, "child"));
            File.WriteAllText(Path.Combine(destinationPath, "child", "replacement.bin"), "replacement");
        });

        var result = await fixture.ExecuteAsync([candidate], mutationObserver: observer);

        var failed = Assert.Single(result.Items);
        Assert.Equal(CleanupOutcome.Failed, failed.Outcome);
        Assert.Contains("snapshot", failed.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(originalChild, "original.bin")));
        Assert.True(File.Exists(Path.Combine(candidate.AbsolutePath, "child", "replacement.bin")));
    }

    [Fact]
    public async Task Descendant_insertion_at_final_boundary_is_detected_before_deletion()
    {
        using var fixture = await CleanupFixture.CreateAsync();
        fixture.Repository.WriteBytes("obj/child/original.bin", 3);
        var candidate = await fixture.ScanSingleAsync();
        var observer = new TestMutationObserver(beforeRecursiveDelete: (_, _, destinationPath) =>
            File.WriteAllText(Path.Combine(destinationPath, "child", "inserted.bin"), "inserted"));

        var result = await fixture.ExecuteAsync([candidate], mutationObserver: observer);

        var failed = Assert.Single(result.Items);
        Assert.Equal(CleanupOutcome.Failed, failed.Outcome);
        Assert.Contains("snapshot", failed.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(candidate.AbsolutePath, "child", "inserted.bin")));
    }

    [Fact]
    public async Task Descendant_removal_at_final_boundary_is_detected_before_deletion()
    {
        using var fixture = await CleanupFixture.CreateAsync();
        fixture.Repository.WriteBytes("obj/child/original.bin", 3);
        var candidate = await fixture.ScanSingleAsync();
        var removedChild = fixture.Temporary.GetPath("removed-child");
        var observer = new TestMutationObserver(beforeRecursiveDelete: (_, _, destinationPath) =>
            Directory.Move(Path.Combine(destinationPath, "child"), removedChild));

        var result = await fixture.ExecuteAsync([candidate], mutationObserver: observer);

        var failed = Assert.Single(result.Items);
        Assert.Equal(CleanupOutcome.Failed, failed.Outcome);
        Assert.Contains("snapshot", failed.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(removedChild, "original.bin")));
        Assert.True(File.Exists(Path.Combine(candidate.AbsolutePath, "artifact.bin")));
    }

    [Fact]
    public async Task Child_mount_change_at_final_boundary_stops_before_traversal_and_preserves_content()
    {
        using var fixture = await CleanupFixture.CreateAsync();
        fixture.Repository.WriteBytes("obj/child/nested.bin", 3);
        var candidate = await fixture.ScanSingleAsync();
        var identityProvider = new ToggleMountIdentityProvider();
        var observer = new TestMutationObserver(beforeRecursiveDelete: (_, _, destinationPath) =>
            identityProvider.ForeignMountPath = Path.Combine(destinationPath, "child"));

        var result = await fixture.ExecuteAsync(
            [candidate],
            mutationObserver: observer,
            identityProvider: identityProvider);

        var failed = Assert.Single(result.Items);
        Assert.Equal(CleanupOutcome.Failed, failed.Outcome);
        Assert.Contains("mount", failed.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(candidate.AbsolutePath, "child", "nested.bin")));
    }

    [Fact]
    public async Task Payload_replacement_before_recovery_is_stranded_and_never_moved_to_the_original_path()
    {
        using var fixture = await CleanupFixture.CreateAsync();
        var candidate = await fixture.ScanSingleAsync();
        var ownedPayload = fixture.Temporary.GetPath("owned-payload");
        string? destination = null;
        var observer = new TestMutationObserver(beforeMovedIdentityCheck: (_, _, destinationPath) =>
        {
            destination = destinationPath;
            Directory.Move(destinationPath, ownedPayload);
            Directory.CreateDirectory(destinationPath);
            File.WriteAllText(Path.Combine(destinationPath, "unrelated.bin"), "unrelated");
        });

        var result = await fixture.ExecuteAsync([candidate], mutationObserver: observer);

        var failed = Assert.Single(result.Items);
        Assert.Equal(CleanupOutcome.Failed, failed.Outcome);
        Assert.NotNull(destination);
        Assert.Contains(destination, failed.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(candidate.AbsolutePath));
        Assert.True(File.Exists(Path.Combine(ownedPayload, "artifact.bin")));
        Assert.True(File.Exists(Path.Combine(destination, "unrelated.bin")));
    }

    [Fact]
    public async Task Repository_move_after_ownership_reports_the_exact_relocated_payload_path()
    {
        using var fixture = await CleanupFixture.CreateAsync();
        var candidate = await fixture.ScanSingleAsync();
        var relocatedRepository = fixture.Temporary.GetPath("repo-relocated-after-ownership");
        string? relocatedPayload = null;
        var observer = new TestMutationObserver(beforeMovedIdentityCheck: (_, _, destinationPath) =>
        {
            relocatedPayload = Path.Combine(
                relocatedRepository,
                Path.GetRelativePath(fixture.Repository.Path, destinationPath));
            Directory.Move(fixture.Repository.Path, relocatedRepository);
        });

        var result = await fixture.ExecuteAsync([candidate], mutationObserver: observer);

        var failed = Assert.Single(result.Items);
        Assert.Equal(CleanupOutcome.Failed, failed.Outcome);
        Assert.NotNull(relocatedPayload);
        Assert.Contains(relocatedPayload, failed.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(relocatedPayload, "artifact.bin")));
        Assert.False(Directory.Exists(candidate.AbsolutePath));
    }

    [Fact]
    public async Task Payload_replacement_immediately_before_reverse_move_is_stranded_and_never_recovered()
    {
        using var fixture = await CleanupFixture.CreateAsync();
        var candidate = await fixture.ScanSingleAsync();
        var fileSystem = new InterceptingCleanupFileSystem(candidate.AbsolutePath);
        var ownedPayload = fixture.Temporary.GetPath("owned-before-reverse-move");
        string? destination = null;
        var observer = new TestMutationObserver(beforeRecoveryMove: (_, _, destinationPath, _) =>
        {
            destination = destinationPath;
            Directory.Move(destinationPath, ownedPayload);
            Directory.CreateDirectory(destinationPath);
            File.WriteAllText(Path.Combine(destinationPath, "unrelated.bin"), "unrelated");
        });

        var result = await fixture.ExecuteAsync(
            [candidate],
            fileSystem: fileSystem,
            mutationObserver: observer);

        var failed = Assert.Single(result.Items);
        Assert.Equal(CleanupOutcome.Failed, failed.Outcome);
        Assert.NotNull(destination);
        Assert.Contains(destination, failed.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(candidate.AbsolutePath));
        Assert.True(File.Exists(Path.Combine(ownedPayload, "artifact.bin")));
        Assert.True(File.Exists(Path.Combine(destination, "unrelated.bin")));
    }

    [Fact]
    public async Task Quarantine_is_created_inside_the_writable_repository_not_the_broad_requested_root()
    {
        using var fixture = await CleanupFixture.CreateAsync();
        var candidate = await fixture.ScanSingleAsync();
        var fileSystem = new RepositoryOnlyQuarantineFileSystem(fixture.Repository.Path, fixture.Temporary.Path);

        var result = await fixture.ExecuteAsync([candidate], fileSystem: fileSystem);

        var deleted = Assert.Single(result.Items);
        Assert.Equal(CleanupOutcome.Deleted, deleted.Outcome);
        Assert.Equal(fixture.Repository.Path, fileSystem.QuarantineParent);
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
        Assert.False(Directory.Exists(candidates[1].AbsolutePath));
        var quarantine = Assert.Single(Directory.GetDirectories(fixture.Repository.Path, ".devcleaner-quarantine-*"));
        var strandedPayload = Path.Combine(quarantine, "payload");
        Assert.True(Directory.Exists(strandedPayload));
        Assert.Contains(strandedPayload, result.Items[1].Message, StringComparison.Ordinal);
        Assert.Contains("partial", result.Items[1].Message, StringComparison.OrdinalIgnoreCase);
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
        Assert.True(failed.DeletionCompleted);
        Assert.Equal(1, result.DeletedCount);
        Assert.Equal(candidate.EstimatedBytes, result.EstimatedDeletedBytes);
        Assert.Contains("empty quarantine", failed.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(candidate.AbsolutePath));
        var quarantine = Assert.Single(Directory.GetDirectories(fixture.Repository.Path, ".devcleaner-quarantine-*"));
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
        private readonly RuleCatalog rules;

        private CleanupFixture(TemporaryDirectory temporary, GitTestRepository repository, RuleCatalog rules)
        {
            Temporary = temporary;
            Repository = repository;
            this.rules = rules;
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
            return new CleanupFixture(temporary, repository, RuleCatalog.Create(DevCleanerConfig.Default));
        }

        public static async Task<CleanupFixture> CreateFileCandidateAsync()
        {
            var temporary = new TemporaryDirectory();
            var repository = await GitTestRepository.CreateAsync(temporary.GetPath("file-repo"));
            repository.Write("project.csproj", "<Project />");
            repository.Write(".gitignore", "artifact.cache\n");
            repository.WriteBytes("artifact.cache", 5);
            await repository.CommitAllAsync();
            var rule = new ArtifactRule("test.file-cache", ArtifactCategory.Cache, ["artifact.cache"], [], true);
            var rules = RuleCatalog.Create(new DevCleanerConfig { CustomRules = [rule] });
            return new CleanupFixture(temporary, repository, rules);
        }

        public async Task<IReadOnlyList<ArtifactCandidate>> ScanAsync()
        {
            var result = await new RepositoryScanner(git).ScanAsync(
                [Repository.Path],
                rules);
            return result.Repositories.SelectMany(repository => repository.Candidates).OrderBy(candidate => candidate.AbsolutePath, StringComparer.Ordinal).ToArray();
        }

        public async Task<ArtifactCandidate> ScanSingleAsync() => Assert.Single(await ScanAsync());

        public Task<CleanupResult> ExecuteAsync(
            IReadOnlyList<ArtifactCandidate> candidates,
            bool dryRun = false,
            IReadOnlyList<string>? requestedRoots = null,
            CancellationToken cancellationToken = default,
            ICleanupFileSystem? fileSystem = null,
            ICleanupMutationObserver? mutationObserver = null,
            IAtomicFileMover? atomicFileMover = null,
            IFileSystemIdentityProvider? identityProvider = null,
            GitClient? cleanupGit = null)
        {
            var service = new CleanupService(
                cleanupGit ?? git,
                fileSystem: fileSystem,
                mutationObserver: mutationObserver,
                identityProvider: identityProvider,
                atomicFileMover: atomicFileMover);
            return service.ExecuteAsync(
                new CleanupRequest(candidates, requestedRoots ?? [Temporary.Path], rules, dryRun),
                cancellationToken);
        }

        public void Dispose() => Temporary.Dispose();
    }

    private sealed class TestMutationObserver(
        Action<ArtifactCandidate, string, string>? beforeQuarantineMove = null,
        Action<ArtifactCandidate, string, string>? beforeMovedIdentityCheck = null,
        Action<ArtifactCandidate, string, string>? beforeRecursiveDelete = null,
        Action<ArtifactCandidate, string, string, string>? beforeRecoveryMove = null) : ICleanupMutationObserver
    {
        public void BeforeQuarantineMove(ArtifactCandidate candidate, string quarantineRoot, string destinationPath) =>
            beforeQuarantineMove?.Invoke(candidate, quarantineRoot, destinationPath);

        public void BeforeMovedIdentityCheck(ArtifactCandidate candidate, string quarantineRoot, string destinationPath) =>
            beforeMovedIdentityCheck?.Invoke(candidate, quarantineRoot, destinationPath);

        public void BeforeRecursiveDelete(ArtifactCandidate candidate, string quarantineRoot, string destinationPath) =>
            beforeRecursiveDelete?.Invoke(candidate, quarantineRoot, destinationPath);

        public void BeforeRecoveryMove(
            ArtifactCandidate candidate,
            string quarantineRoot,
            string destinationPath,
            string candidatePath) =>
            beforeRecoveryMove?.Invoke(candidate, quarantineRoot, destinationPath, candidatePath);
    }

    private sealed class FailingAtomicFileMover : IAtomicFileMover
    {
        public int MoveCalls { get; private set; }

        public void MoveNoCopy(string sourcePath, string destinationPath)
        {
            MoveCalls++;
            throw new IOException("Injected atomic move failure.");
        }
    }

    private sealed class RecordingAtomicFileMover : IAtomicFileMover
    {
        private readonly NativeAtomicFileMover inner = new();

        public int MoveCalls { get; private set; }

        public void MoveNoCopy(string sourcePath, string destinationPath)
        {
            MoveCalls++;
            inner.MoveNoCopy(sourcePath, destinationPath);
        }
    }

    private sealed class InterceptingIdentityProvider(EarlyQuarantineFailure failure) : IFileSystemIdentityProvider
    {
        private readonly FileSystemIdentityProvider inner = new();

        public bool TryGetIdentity(string path, out FileSystemIdentity? identity, out string? error)
        {
            if (IsQuarantineRoot(path) && failure == EarlyQuarantineFailure.IdentityUnavailable)
            {
                identity = null;
                error = "Injected quarantine identity failure.";
                return false;
            }

            if (!inner.TryGetIdentity(path, out identity, out error) || identity is null) return false;
            if (IsQuarantineRoot(path) && failure == EarlyQuarantineFailure.MountMismatch)
            {
                identity = identity with { VolumeId = identity.VolumeId + 1, MountId = "injected-other-mount" };
            }

            return true;
        }

        public bool TryGetMountIdentity(string path, out FileSystemMountIdentity? identity, out string? error) =>
            inner.TryGetMountIdentity(path, out identity, out error);

        private static bool IsQuarantineRoot(string path) =>
            Path.GetFileName(path).StartsWith(".devcleaner-quarantine-", StringComparison.Ordinal);
    }

    private sealed class ToggleMountIdentityProvider : IFileSystemIdentityProvider
    {
        private readonly FileSystemIdentityProvider inner = new();

        public string? ForeignMountPath { get; set; }

        public bool TryGetIdentity(string path, out FileSystemIdentity? identity, out string? error)
        {
            if (!inner.TryGetIdentity(path, out identity, out error) || identity is null) return false;
            if (ForeignMountPath is not null &&
                string.Equals(Path.GetFullPath(path), Path.GetFullPath(ForeignMountPath), StringComparison.Ordinal))
            {
                identity = identity with { MountId = "injected-foreign-mount" };
            }

            return true;
        }

        public bool TryGetMountIdentity(string path, out FileSystemMountIdentity? identity, out string? error)
        {
            if (!TryGetIdentity(path, out var fileIdentity, out error) || fileIdentity is null)
            {
                identity = null;
                return false;
            }

            identity = new FileSystemMountIdentity(fileIdentity.VolumeId, fileIdentity.MountId);
            return true;
        }
    }

    public enum EarlyQuarantineFailure
    {
        IdentityUnavailable,
        MountMismatch,
        AtomicMove,
    }

    private sealed class InterceptingCleanupFileSystem : ICleanupFileSystem
    {
        private readonly SystemCleanupFileSystem inner = new();
        private readonly string? failingPath;
        private readonly CancellationTokenSource? cancelAfterDirectoryDelete;
        private readonly int cancelAfterFileDeleteOnCall;
        private readonly CancellationTokenSource? cancellation;
        private readonly bool failQuarantineCleanup;
        private readonly Func<string, bool>? failGetAttributes;
        private int ownedDeleteCalls;
        private bool deletionFailureInjected;

        public InterceptingCleanupFileSystem(
            string? failingPath = null,
            CancellationTokenSource? cancelAfterDirectoryDelete = null,
            int cancelAfterFileDeleteOnCall = 0,
            CancellationTokenSource? cancellation = null,
            bool failQuarantineCleanup = false,
            Func<string, bool>? failGetAttributes = null)
        {
            this.failingPath = failingPath;
            this.cancelAfterDirectoryDelete = cancelAfterDirectoryDelete;
            this.cancelAfterFileDeleteOnCall = cancelAfterFileDeleteOnCall;
            this.cancellation = cancellation;
            this.failQuarantineCleanup = failQuarantineCleanup;
            this.failGetAttributes = failGetAttributes;
        }

        public FileAttributes GetAttributes(string path)
        {
            if (failGetAttributes?.Invoke(path) == true)
            {
                throw new UnauthorizedAccessException("Injected recovery inspection failure.");
            }

            return inner.GetAttributes(path);
        }

        public IReadOnlyList<string> GetFileSystemEntries(string path) => inner.GetFileSystemEntries(path);

        public void CreateDirectory(string path) => inner.CreateDirectory(path);

        public void DeleteFile(string path)
        {
            ownedDeleteCalls++;
            if (failingPath is not null && !deletionFailureInjected)
            {
                deletionFailureInjected = true;
                throw new IOException("Injected deletion failure.");
            }

            if (ownedDeleteCalls == cancelAfterFileDeleteOnCall)
            {
                inner.DeleteFile(path);
                cancellation?.Cancel();
                throw new OperationCanceledException(cancellation?.Token ?? CancellationToken.None);
            }

            inner.DeleteFile(path);
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

    private sealed class RepositoryOnlyQuarantineFileSystem(string repositoryRoot, string broadRoot) : ICleanupFileSystem
    {
        private readonly SystemCleanupFileSystem inner = new();

        public string? QuarantineParent { get; private set; }

        public FileAttributes GetAttributes(string path) => inner.GetAttributes(path);

        public IReadOnlyList<string> GetFileSystemEntries(string path) => inner.GetFileSystemEntries(path);

        public void CreateDirectory(string path)
        {
            if (Path.GetFileName(path).StartsWith(".devcleaner-quarantine-", StringComparison.Ordinal))
            {
                QuarantineParent = Path.GetDirectoryName(path);
                if (string.Equals(QuarantineParent, Path.GetFullPath(broadRoot), StringComparison.Ordinal))
                {
                    throw new UnauthorizedAccessException("Injected broad-root write denial.");
                }

                Assert.Equal(Path.GetFullPath(repositoryRoot), QuarantineParent);
            }

            inner.CreateDirectory(path);
        }

        public void DeleteFile(string path) => inner.DeleteFile(path);

        public void DeleteDirectory(string path) => inner.DeleteDirectory(path);
    }
}
