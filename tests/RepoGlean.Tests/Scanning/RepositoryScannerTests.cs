using System.Diagnostics;
using RepoGlean.Cli;
using RepoGlean.Configuration;
using RepoGlean.Git;
using RepoGlean.Progress;
using RepoGlean.Rules;
using RepoGlean.Scanning;
using RepoGlean.Tests.Support;

namespace RepoGlean.Tests.Scanning;

public sealed class RepositoryScannerTests
{
    [Fact]
    public async Task ScanAsync_propagates_the_newest_artifact_write_time()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await GitTestRepository.CreateAsync(temporary.GetPath("repo"));
        repository.Write("project.csproj", "<Project />");
        repository.Write(".gitignore", "obj/\n");
        repository.Write("obj/artifact.bin", "payload");
        await repository.CommitAllAsync();
        var expected = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);
        var candidatePath = repository.GetPath("obj");
        File.SetLastWriteTimeUtc(repository.GetPath("obj/artifact.bin"), expected.UtcDateTime.AddDays(-1));
        File.SetLastWriteTimeUtc(candidatePath, expected.UtcDateTime);

        var result = await ScanAsync(new GitClient(), repository);

        var candidate = Assert.Single(result.Repositories.Single().Candidates);
        Assert.Equal(expected, candidate.NewestWriteTimeUtc);
    }

    [Fact]
    public async Task ScanAsync_reports_truthful_repository_progress_and_matching_warnings()
    {
        using var temporary = new TemporaryDirectory();
        var withCandidate = await CreateDotnetRepositoryAsync(temporary.GetPath("with-candidate"), 12);
        var withoutCandidate = await GitTestRepository.CreateAsync(temporary.GetPath("without-candidate"));
        withoutCandidate.Write("README.md", "ordinary repository");
        await withoutCandidate.CommitAllAsync();
        withoutCandidate.Write(".RepoGlean-Quarantine-0123456789abcdef/payload.bin", "stranded");
        var progress = new RecordingProgress();
        var scanner = new RepositoryScanner(new GitClient(), progress, ProgressOperation.Clean);

        var result = await scanner.ScanAsync(
            [withCandidate.Path, withoutCandidate.Path],
            RuleCatalog.Create(RepoGleanConfig.Default));

        var starts = progress.Events
            .Where(item => item.Kind == ProgressEventKind.RepositoryScanStarted)
            .ToArray();
        Assert.Equal([withCandidate.Path, withoutCandidate.Path], starts.Select(item => item.Path));
        Assert.Equal([1, 2], starts.Select(item => item.Current));
        Assert.All(starts, item => Assert.Equal(2, item.Total));

        var completions = progress.Events
            .Where(item => item.Kind == ProgressEventKind.RepositoryScanCompleted)
            .ToArray();
        Assert.Equal([withCandidate.Path, withoutCandidate.Path], completions.Select(item => item.Path));
        Assert.Equal([1, 2], completions.Select(item => item.Current));
        Assert.All(completions, item => Assert.Equal(2, item.Total));
        Assert.Equal([1L, 0L], completions.Select(item => item.CurrentCandidateCount));
        Assert.Equal([12L, 0L], completions.Select(item => item.CurrentEstimatedBytes));
        Assert.Equal([1L, 1L], completions.Select(item => item.CandidateCount));
        Assert.Equal([12L, 12L], completions.Select(item => item.EstimatedBytes));
        Assert.Equal([0L, 1L], completions.Select(item => item.WarningCount));
        Assert.True(completions
            .Zip(completions.Skip(1))
            .All(pair =>
                pair.First.CandidateCount <= pair.Second.CandidateCount &&
                pair.First.EstimatedBytes <= pair.Second.EstimatedBytes));

        var warning = Assert.Single(result.Warnings);
        var warningEvent = Assert.Single(progress.Events, item => item.Kind == ProgressEventKind.Warning);
        Assert.Equal(warning.Path, warningEvent.Path);
        Assert.Equal(warning.Message, warningEvent.Message);
        Assert.All(progress.Events, item => Assert.Equal(ProgressOperation.Clean, item.Operation));
        Assert.DoesNotContain(progress.Events, item =>
            item.Kind is ProgressEventKind.CandidateStarted or ProgressEventKind.CandidateCompleted);
    }

    [Fact]
    public async Task ScanAsync_preserves_results_and_warnings_when_progress_reporting_fails()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await CreateDotnetRepositoryAsync(temporary.GetPath("repository"), 12);
        repository.Write(".RepoGlean-Quarantine-0123456789abcdef/payload.bin", "stranded");
        var progress = new ThrowingProgress(new InvalidOperationException("injected progress failure"));
        var scanner = new RepositoryScanner(new GitClient(), progress, ProgressOperation.Scan);

        var result = await scanner.ScanAsync(
            [repository.Path],
            RuleCatalog.Create(RepoGleanConfig.Default));

        var scannedRepository = Assert.Single(result.Repositories);
        var candidate = Assert.Single(scannedRepository.Candidates);
        Assert.Equal(12, candidate.EstimatedBytes);
        var warning = Assert.Single(result.Warnings);
        Assert.Equal(repository.GetPath(".RepoGlean-Quarantine-0123456789abcdef"), warning.Path);
        Assert.Equal(
            "Skipped reserved RepoGlean quarantine; inspect or remove the stranded payload manually.",
            warning.Message);
    }

    [Fact]
    public async Task ScanAsync_does_not_swallow_catastrophic_progress_failures()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await GitTestRepository.CreateAsync(temporary.GetPath("repository"));
        var failure = new OutOfMemoryException("injected catastrophic progress failure");
        var progress = new ThrowingProgress(failure);
        var scanner = new RepositoryScanner(new GitClient(), progress, ProgressOperation.Scan);

        var exception = await Assert.ThrowsAsync<OutOfMemoryException>(() =>
            scanner.ScanAsync([repository.Path], RuleCatalog.Create(RepoGleanConfig.Default)));

        Assert.Same(failure, exception);
    }

    [Fact]
    public async Task ScanAsync_normalizes_repository_aliases_before_counting_distinct_roots()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await GitTestRepository.CreateAsync(temporary.GetPath("repository"));
        var relativeAlias = Path.GetRelativePath(Directory.GetCurrentDirectory(), repository.Path);
        var progress = new RecordingProgress();
        var scanner = new RepositoryScanner(new GitClient(), progress, ProgressOperation.Scan);

        var result = await scanner.ScanAsync(
            [repository.Path, relativeAlias],
            RuleCatalog.Create(RepoGleanConfig.Default));

        var scannedRepository = Assert.Single(result.Repositories);
        Assert.Equal(repository.Path, scannedRepository.RepositoryRoot);
        var started = Assert.Single(
            progress.Events,
            item => item.Kind == ProgressEventKind.RepositoryScanStarted);
        Assert.Equal(1, started.Current);
        Assert.Equal(1, started.Total);
        var completed = Assert.Single(
            progress.Events,
            item => item.Kind == ProgressEventKind.RepositoryScanCompleted);
        Assert.Equal(1, completed.Current);
        Assert.Equal(1, completed.Total);
    }

    [Fact]
    public async Task ScanAsync_uses_nested_gitignore_info_exclude_and_global_excludes()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await GitTestRepository.CreateAsync(temporary.GetPath("repo"));
        repository.Write("project.csproj", "<Project />");
        repository.Write(".gitignore", "src/one/obj/\n");
        repository.Write("src/one/obj/a.bin", "a");
        repository.Write("src/two/.gitignore", "obj/\n");
        repository.Write("src/two/obj/b.bin", "bb");
        await repository.CommitAllAsync();
        repository.Write(".git/info/exclude", "src/three/obj/\n");
        repository.Write("src/three/obj/c.bin", "ccc");

        var globalIgnore = temporary.GetPath("global-ignore");
        File.WriteAllText(globalIgnore, "src/four/obj/\n");
        var globalConfig = temporary.GetPath("global-config");
        await GitTestRepository.RunAsync("git", temporary.Path, new Dictionary<string, string?> { ["GIT_CONFIG_GLOBAL"] = globalConfig },
            "config", "--global", "core.excludesFile", globalIgnore);
        repository.Write("src/four/obj/d.bin", "dddd");
        var git = new GitClient(environment: new Dictionary<string, string?> { ["GIT_CONFIG_GLOBAL"] = globalConfig });

        var result = await ScanAsync(git, repository);

        Assert.Equal(4, result.Repositories.Single().Candidates.Count);
    }

    [Fact]
    public async Task ScanAsync_requires_a_visible_ecosystem_marker_and_ignores_unknown_artifacts()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await GitTestRepository.CreateAsync(temporary.GetPath("repo"));
        repository.Write(".gitignore", "obj/\nunknown.tmp\n");
        repository.Write("obj/a.bin", "artifact");
        repository.Write("unknown.tmp", "unknown");
        await repository.CommitAllAsync();

        var withoutMarker = await ScanAsync(new GitClient(), repository);
        repository.Write("project.csproj", "<Project />");
        await repository.CommitAllAsync("add marker");
        var withMarker = await ScanAsync(new GitClient(), repository);

        Assert.Empty(withoutMarker.Repositories.Single().Candidates);
        var candidate = Assert.Single(withMarker.Repositories.Single().Candidates);
        Assert.Equal("obj", candidate.RelativePath);
    }

    [Fact]
    public async Task ScanAsync_rejects_tracked_content_even_when_a_parent_matches_an_ignore_rule()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await GitTestRepository.CreateAsync(temporary.GetPath("repo"));
        repository.Write("project.csproj", "<Project />");
        repository.Write("obj/tracked.bin", "tracked");
        await repository.CommitAllAsync();
        repository.Write(".gitignore", "obj/\n");
        await repository.CommitAllAsync("ignore artifact");

        var result = await ScanAsync(new GitClient(), repository);

        Assert.Empty(result.Repositories.Single().Candidates);
    }

    [Fact]
    public async Task ScanAsync_collapses_nested_matches_and_sums_logical_file_lengths()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await GitTestRepository.CreateAsync(temporary.GetPath("repo"));
        repository.Write("project.csproj", "<Project />");
        repository.Write(".gitignore", "obj/\n");
        repository.WriteBytes("obj/first.bin", 7);
        repository.WriteBytes("obj/nested/obj/second.bin", 11);
        await repository.CommitAllAsync();

        var result = await ScanAsync(new GitClient(), repository);

        var candidate = Assert.Single(result.Repositories.Single().Candidates);
        Assert.Equal("obj", candidate.RelativePath);
        Assert.Equal(2, candidate.FileCount);
        Assert.Equal(18, candidate.EstimatedBytes);
        Assert.Equal(18, result.EstimatedBytes);
        Assert.NotNull(candidate.Identity);
    }

    [Fact]
    public async Task ScanAsync_reports_dependency_artifacts_but_does_not_preselect_them()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await GitTestRepository.CreateAsync(temporary.GetPath("repo"));
        repository.Write("package.json", "{}");
        repository.Write(".gitignore", "node_modules/\n");
        repository.Write("node_modules/example/index.js", "x");
        await repository.CommitAllAsync();

        var result = await ScanAsync(new GitClient(), repository);

        var candidate = Assert.Single(result.Repositories.Single().Candidates);
        Assert.Equal(ArtifactCategory.Dependency, candidate.Category);
        Assert.False(candidate.Preselected);
    }

    [Fact]
    public async Task ScanAsync_rejects_candidate_links_and_nested_repository_boundaries()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await GitTestRepository.CreateAsync(temporary.GetPath("repo"));
        repository.Write("project.csproj", "<Project />");
        repository.Write(".gitignore", "obj/\nlinked-obj/\n");
        var nested = await GitTestRepository.CreateAsync(repository.GetPath("obj/nested"));
        nested.Write("data.bin", "nested");
        var external = temporary.GetPath("external");
        Directory.CreateDirectory(external);
        File.WriteAllText(System.IO.Path.Combine(external, "large.bin"), new string('x', 100));
        try
        {
            Directory.CreateSymbolicLink(repository.GetPath("linked-obj"), external);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
        {
        }
        await repository.CommitAllAsync();

        var custom = new ArtifactRule("test.linked", ArtifactCategory.Build, ["**/linked-obj", "**/linked-obj/**"], ["**/*.csproj"], true);
        var catalog = new RuleCatalog([.. BuiltInRules.All, custom]);
        var result = await new RepositoryScanner(new GitClient()).ScanAsync([repository.Path], catalog);

        Assert.Empty(result.Repositories.Single().Candidates);
        Assert.Contains(result.Warnings, warning => warning.Message.Contains("repository", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ScanAsync_rejects_an_entire_candidate_containing_a_nested_directory_link()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await GitTestRepository.CreateAsync(temporary.GetPath("repo"));
        repository.Write("project.csproj", "<Project />");
        repository.Write(".gitignore", "obj/\n");
        repository.Write("obj/local.bin", "local");
        var external = temporary.GetPath("external");
        Directory.CreateDirectory(external);
        File.WriteAllText(System.IO.Path.Combine(external, "external.bin"), "external");
        try
        {
            Directory.CreateSymbolicLink(repository.GetPath("obj/nested-link"), external);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
        {
            return;
        }
        await repository.CommitAllAsync();

        var result = await ScanAsync(new GitClient(), repository);

        Assert.Empty(result.Repositories.Single().Candidates);
        Assert.Contains(result.Warnings, warning =>
            warning.Path == repository.GetPath("obj/nested-link") &&
            warning.Message.Contains("link", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ScanAsync_warns_when_a_matching_candidate_root_is_a_directory_link()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await GitTestRepository.CreateAsync(temporary.GetPath("repo"));
        repository.Write("project.csproj", "<Project />");
        repository.Write(".gitignore", "linked-obj/\n");
        var external = temporary.GetPath("external");
        Directory.CreateDirectory(external);
        try
        {
            Directory.CreateSymbolicLink(repository.GetPath("linked-obj"), external);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
        {
            return;
        }
        await repository.CommitAllAsync();
        var rule = new ArtifactRule("test.link", ArtifactCategory.Build, ["**/linked-obj", "**/linked-obj/**"], ["**/*.csproj"], true);

        var result = await new RepositoryScanner(new GitClient()).ScanAsync([repository.Path], new RuleCatalog([rule]));

        Assert.Empty(result.Repositories.Single().Candidates);
        Assert.Contains(result.Warnings, warning =>
            warning.Path == repository.GetPath("linked-obj") &&
            warning.Message.Contains("link", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ScanAsync_applies_repository_category_exclusion_and_minimum_size_filters_and_sorts_by_size()
    {
        using var temporary = new TemporaryDirectory();
        var small = await CreateDotnetRepositoryAsync(temporary.GetPath("small"), 5);
        var large = await CreateDotnetRepositoryAsync(temporary.GetPath("large"), 20);
        var excluded = await CreateDotnetRepositoryAsync(temporary.GetPath("excluded"), 40);
        var scanner = new RepositoryScanner(new GitClient());
        var options = new ScanOptions(
            RepositoryFilters: ["small", "large"],
            CategoryFilters: [ArtifactCategory.Build],
            Exclusions: [small.GetPath("obj")],
            MinimumBytes: 10);

        var result = await scanner.ScanAsync([small.Path, excluded.Path, large.Path], RuleCatalog.Create(RepoGleanConfig.Default), options);

        var repositoryResult = Assert.Single(result.Repositories);
        Assert.Equal(large.Path, repositoryResult.RepositoryRoot);
        Assert.Equal(20, repositoryResult.EstimatedBytes);
    }

    [Fact]
    public async Task ScanAsync_sorts_repositories_and_candidates_by_estimated_bytes_descending()
    {
        using var temporary = new TemporaryDirectory();
        var smaller = await CreateDotnetRepositoryAsync(temporary.GetPath("smaller"), 3);
        var larger = await CreateDotnetRepositoryAsync(temporary.GetPath("larger"), 30);
        larger.Write(".gitignore", "obj/\nsrc/obj/\n");
        larger.WriteBytes("src/obj/small.bin", 2);
        await larger.CommitAllAsync("second artifact");

        var result = await new RepositoryScanner(new GitClient()).ScanAsync(
            [smaller.Path, larger.Path], RuleCatalog.Create(RepoGleanConfig.Default));

        Assert.Equal([larger.Path, smaller.Path], result.Repositories.Select(item => item.RepositoryRoot));
        Assert.Equal([30L, 2L], result.Repositories[0].Candidates.Select(item => item.EstimatedBytes));
    }

    [Fact]
    public async Task GitClient_reports_a_clear_error_when_git_is_missing()
    {
        var exception = await Assert.ThrowsAsync<GitUnavailableException>(() =>
            new GitClient("repoglean-definitely-missing-git").GetVersionAsync());

        Assert.Contains("Git executable", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not found", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ScanAsync_observes_cancellation()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await CreateDotnetRepositoryAsync(temporary.GetPath("repo"), 1);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new RepositoryScanner(new GitClient()).ScanAsync(
                [repository.Path], RuleCatalog.Create(RepoGleanConfig.Default), cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task ProcessRunner_cancellation_terminates_a_running_process_tree()
    {
        if (OperatingSystem.IsWindows()) return;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ProcessRunner("/bin/sh").RunAsync(["-c", "sleep 30 & wait"], null, cancellation.Token));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"Cancellation took {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task ScanAsync_warns_for_a_check_ignore_failure_and_continues_with_other_candidates()
    {
        if (OperatingSystem.IsWindows()) return;
        using var temporary = new TemporaryDirectory();
        var repository = await GitTestRepository.CreateAsync(temporary.GetPath("repo"));
        repository.Write("project.csproj", "<Project />");
        repository.Write(".gitignore", "obj/\nsrc/obj/\n");
        repository.Write("obj/fails.bin", "failure path");
        repository.Write("src/obj/succeeds.bin", "unrelated");
        await repository.CommitAllAsync();
        var wrapper = temporary.GetPath("git-wrapper");
        File.WriteAllText(wrapper, "#!/bin/sh\nif [ \"$3\" = \"check-ignore\" ]; then cat > \"$REPOGLEAN_CHECK_IGNORE_INPUT\"; if tr '\\0' '\\n' < \"$REPOGLEAN_CHECK_IGNORE_INPUT\" | grep -Fxq 'obj'; then echo injected-check-ignore-failure >&2; exit 2; fi; exec git \"$@\" < \"$REPOGLEAN_CHECK_IGNORE_INPUT\"; fi\nexec git \"$@\"\n");
        File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var result = await ScanAsync(new GitClient(wrapper, new Dictionary<string, string?>
        {
            ["REPOGLEAN_CHECK_IGNORE_INPUT"] = temporary.GetPath("check-ignore-input"),
        }), repository);

        var candidate = Assert.Single(result.Repositories.Single().Candidates);
        Assert.Equal("src/obj", candidate.RelativePath);
        Assert.Contains(result.Warnings, warning =>
            warning.Path == repository.GetPath("obj") &&
            warning.Message.Contains("injected-check-ignore-failure", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ScanAsync_prunes_prior_repository_local_quarantines_and_never_rediscovers_their_payloads()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await GitTestRepository.CreateAsync(temporary.GetPath("repo"));
        repository.Write(".gitignore", "node_modules/\n");
        repository.Write("package.json", "{}");
        await repository.CommitAllAsync();
        const string quarantineName = ".RepoGlean-Quarantine-0123456789abcdef";
        var quarantinePath = repository.GetPath(quarantineName);
        repository.Write($"{quarantineName}/payload/package.json", "{}");
        repository.Write($"{quarantineName}/payload/node_modules/package.bin", "stranded");

        var result = await ScanAsync(new GitClient(), repository);

        var scannedRepository = Assert.Single(result.Repositories);
        Assert.Empty(scannedRepository.Candidates);
        Assert.Contains(result.Warnings, warning =>
            warning.Path == quarantinePath &&
            warning.Message.Contains("quarantine", StringComparison.OrdinalIgnoreCase));
        Assert.True(File.Exists(Path.Combine(quarantinePath, "payload", "node_modules", "package.bin")));
    }

    [Fact]
    public async Task ScanAsync_batches_git_ignore_checks_at_scale()
    {
        if (OperatingSystem.IsWindows()) return;
        using var temporary = new TemporaryDirectory();
        var repository = await GitTestRepository.CreateAsync(temporary.GetPath("repo"));
        repository.Write(".gitignore", "artifact-*/\n");
        const int candidateCount = 129;
        for (var index = 0; index < candidateCount; index++)
        {
            repository.Write($"artifact-{index:D3}/payload.bin", "ignored");
        }

        await repository.CommitAllAsync();
        var invocationLog = temporary.GetPath("check-ignore-invocations");
        var wrapper = temporary.GetPath("git-wrapper");
        File.WriteAllText(wrapper, "#!/bin/sh\nif [ \"$3\" = \"check-ignore\" ]; then printf 'check-ignore\\n' >> \"$REPOGLEAN_CHECK_IGNORE_LOG\"; fi\nexec git \"$@\"\n");
        File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var git = new GitClient(wrapper, new Dictionary<string, string?>
        {
            ["REPOGLEAN_CHECK_IGNORE_LOG"] = invocationLog,
        });
        var rule = new ArtifactRule("test.scaled", ArtifactCategory.Build, ["artifact-*"], [], true);

        var result = await new RepositoryScanner(git).ScanAsync([repository.Path], new RuleCatalog([rule]));

        Assert.Equal(candidateCount, result.Repositories.Single().Candidates.Count);
        Assert.Equal(2, File.ReadAllLines(invocationLog).Length);
    }

    [Fact]
    public async Task GitClient_batches_nul_delimited_ignore_paths_without_losing_spaces_or_newlines()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await GitTestRepository.CreateAsync(temporary.GetPath("repo"));
        repository.Write(".gitignore", "cache*/\n");
        repository.Write("visible/file.bin", "visible");
        await repository.CommitAllAsync();
        repository.Write("cache space/payload.bin", "ignored with a space");
        var paths = new List<string> { "cache space", "visible" };
        var expected = new HashSet<string>(["cache space"], StringComparer.Ordinal);
        if (!OperatingSystem.IsWindows())
        {
            repository.Write("cache\nline/payload.bin", "ignored with a newline");
            paths.Add("cache\nline");
            expected.Add("cache\nline");
        }

        var ignoredPaths = await new GitClient().GetIgnoredPathsAsync(repository.Path, paths);

        Assert.Equal(expected, ignoredPaths);
    }

    [Fact]
    public async Task ScanAsync_warns_for_a_repository_git_failure_and_continues_with_other_repositories()
    {
        using var temporary = new TemporaryDirectory();
        var valid = await CreateDotnetRepositoryAsync(temporary.GetPath("valid"), 10);
        var broken = temporary.GetPath("broken");
        Directory.CreateDirectory(broken);
        File.WriteAllText(System.IO.Path.Combine(broken, ".git"), "gitdir: missing-git-directory\n");

        var result = await new RepositoryScanner(new GitClient()).ScanAsync(
            [broken, valid.Path], RuleCatalog.Create(RepoGleanConfig.Default));

        var repository = Assert.Single(result.Repositories);
        Assert.Equal(valid.Path, repository.RepositoryRoot);
        Assert.Contains(result.Warnings, warning =>
            warning.Path == broken &&
            warning.Message.Contains("rev-parse", StringComparison.OrdinalIgnoreCase));
    }

    private static Task<ScanResult> ScanAsync(GitClient git, GitTestRepository repository) =>
        new RepositoryScanner(git).ScanAsync([repository.Path], RuleCatalog.Create(RepoGleanConfig.Default));

    private static async Task<GitTestRepository> CreateDotnetRepositoryAsync(string path, int artifactSize)
    {
        var repository = await GitTestRepository.CreateAsync(path);
        repository.Write("project.csproj", "<Project />");
        repository.Write(".gitignore", "obj/\n");
        repository.WriteBytes("obj/artifact.bin", artifactSize);
        await repository.CommitAllAsync();
        return repository;
    }
}
