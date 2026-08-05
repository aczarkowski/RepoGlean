using RepoGlean.Auditing;
using RepoGlean.Cli;
using RepoGlean.Configuration;
using RepoGlean.Git;
using RepoGlean.Progress;
using RepoGlean.Rules;
using RepoGlean.Scanning;
using RepoGlean.Tests.Support;

namespace RepoGlean.Tests.Auditing;

public sealed class RepositoryAuditorTests
{
    [Fact]
    public async Task Audit_collapses_unknown_storage_and_carves_classified_and_visible_branches()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await GitTestRepository.CreateAsync(temporary.GetPath("repo"));
        repository.Write("project.csproj", "<Project />");
        repository.Write(".gitignore", "unknown/\nobj/\n");
        repository.Write("unknown/tracked.txt", "keep");
        await repository.GitAsync("add", "-f", "unknown/tracked.txt");
        await repository.CommitAllAsync();
        repository.WriteBytes("unknown/a.bin", 11);
        repository.WriteBytes("unknown/nested/b.bin", 13);
        repository.WriteBytes("unknown/obj/classified.bin", 17);

        var result = await AuditAsync(new GitClient(), [repository.Path], RuleCatalog.Create(RepoGleanConfig.Default));

        var finding = Assert.Single(Assert.Single(result.Repositories).Findings);
        Assert.Equal("unknown", finding.RelativePath);
        Assert.Equal(2, finding.FileCount);
        Assert.Equal(24, finding.EstimatedBytes);
        Assert.Equal("unknown/", finding.Ignore.Pattern);
        Assert.Equal(2, result.FileCount);
        Assert.Equal(24, result.EstimatedBytes);
    }

    [Fact]
    public async Task Audit_keeps_sibling_ignored_roots_as_separate_findings()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await GitTestRepository.CreateAsync(temporary.GetPath("repo"));
        repository.Write(".gitignore", "alpha/\nbeta/\n");
        await repository.CommitAllAsync();
        repository.WriteBytes("alpha/a.bin", 7);
        repository.WriteBytes("beta/b.bin", 11);

        var result = await AuditAsync(new GitClient(), [repository.Path], RuleCatalog.Create(RepoGleanConfig.Default));

        Assert.Equal(["beta", "alpha"], result.Repositories.Single().Findings.Select(item => item.RelativePath));
        Assert.Equal(2, result.FileCount);
        Assert.Equal(18, result.EstimatedBytes);
    }

    [Fact]
    public async Task Audit_carves_active_builtin_and_custom_rule_matches()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await GitTestRepository.CreateAsync(temporary.GetPath("repo"));
        repository.Write("project.csproj", "<Project />");
        repository.Write(".gitignore", "unknown/\n");
        await repository.CommitAllAsync();
        repository.WriteBytes("unknown/keep.bin", 5);
        repository.WriteBytes("unknown/obj/built-in.bin", 7);
        repository.WriteBytes("unknown/generated/custom.bin", 11);
        var custom = new ArtifactRule(
            "test.generated",
            ArtifactCategory.Build,
            ["**/generated", "**/generated/**"],
            ["**/*.csproj"],
            true);
        var catalog = new RuleCatalog([.. BuiltInRules.All, custom]);

        var result = await AuditAsync(new GitClient(), [repository.Path], catalog);

        var finding = Assert.Single(result.Repositories.Single().Findings);
        Assert.Equal("unknown", finding.RelativePath);
        Assert.Equal(1, finding.FileCount);
        Assert.Equal(5, finding.EstimatedBytes);
    }

    [Fact]
    public async Task Audit_treats_a_disabled_builtin_rule_as_unclassified()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await GitTestRepository.CreateAsync(temporary.GetPath("repo"));
        repository.Write("project.csproj", "<Project />");
        repository.Write(".gitignore", "obj/\n");
        await repository.CommitAllAsync();
        repository.WriteBytes("obj/artifact.bin", 17);
        var catalog = RuleCatalog.Create(new RepoGleanConfig { DisabledRules = ["dotnet.obj"] });

        var result = await AuditAsync(new GitClient(), [repository.Path], catalog);

        var finding = Assert.Single(result.Repositories.Single().Findings);
        Assert.Equal("obj", finding.RelativePath);
        Assert.Equal(17, finding.EstimatedBytes);
    }

    [Fact]
    public async Task Audit_finds_an_ignored_child_below_a_visible_parent()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await GitTestRepository.CreateAsync(temporary.GetPath("repo"));
        repository.Write(".gitignore", "src/cache/\n");
        repository.Write("src/tracked.txt", "visible");
        await repository.CommitAllAsync();
        repository.WriteBytes("src/cache/payload.bin", 13);

        var result = await AuditAsync(new GitClient(), [repository.Path], RuleCatalog.Create(RepoGleanConfig.Default));

        var finding = Assert.Single(result.Repositories.Single().Findings);
        Assert.Equal("src/cache", finding.RelativePath);
        Assert.Equal(13, finding.EstimatedBytes);
    }

    [Fact]
    public async Task Audit_preserves_git_negation_when_classifying_without_index_suppression()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await GitTestRepository.CreateAsync(temporary.GetPath("repo"));
        repository.Write(".gitignore", "cache/\n!cache/\ncache/*\n!cache/keep/\n");
        await repository.CommitAllAsync();
        repository.WriteBytes("cache/drop.bin", 7);
        repository.WriteBytes("cache/keep/visible.bin", 11);

        var result = await AuditAsync(new GitClient(), [repository.Path], RuleCatalog.Create(RepoGleanConfig.Default));

        var finding = Assert.Single(result.Repositories.Single().Findings);
        Assert.Equal("cache/drop.bin", finding.RelativePath);
        Assert.Equal(7, finding.EstimatedBytes);
        Assert.Equal("cache/*", finding.Ignore.Pattern);
    }

    [Fact]
    public async Task Audit_applies_the_threshold_after_carving_classified_content()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await GitTestRepository.CreateAsync(temporary.GetPath("repo"));
        repository.Write("project.csproj", "<Project />");
        repository.Write(".gitignore", "unknown/\n");
        await repository.CommitAllAsync();
        repository.WriteBytes("unknown/unclassified.bin", 11);
        repository.WriteBytes("unknown/obj/classified.bin", 17);

        var result = await new RepositoryAuditor(new GitClient()).AuditAsync(
            [repository.Path],
            RuleCatalog.Create(RepoGleanConfig.Default),
            new AuditOptions([], [], 12));

        Assert.Empty(result.Repositories.Single().Findings);
        Assert.Equal(0, result.EstimatedBytes);
    }

    [Fact]
    public async Task Audit_keeps_successful_empty_repositories_and_omits_empty_ignored_directories()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await GitTestRepository.CreateAsync(temporary.GetPath("repo"));
        repository.Write(".gitignore", "empty/\n");
        await repository.CommitAllAsync();
        Directory.CreateDirectory(repository.GetPath("empty"));

        var result = await AuditAsync(new GitClient(), [repository.Path], RuleCatalog.Create(RepoGleanConfig.Default));

        var audited = Assert.Single(result.Repositories);
        Assert.Equal(repository.Path, audited.RepositoryRoot);
        Assert.Empty(audited.Findings);
        Assert.Equal(0, audited.FileCount);
        Assert.Equal(0, audited.EstimatedBytes);
    }

    [Fact]
    public async Task Audit_warns_for_a_broken_repository_without_discarding_valid_results()
    {
        using var temporary = new TemporaryDirectory();
        var valid = await GitTestRepository.CreateAsync(temporary.GetPath("valid"));
        valid.Write(".gitignore", "unknown/\n");
        await valid.CommitAllAsync();
        valid.WriteBytes("unknown/data.bin", 9);
        var broken = temporary.GetPath("broken");
        Directory.CreateDirectory(broken);
        File.WriteAllText(Path.Combine(broken, ".git"), "gitdir: missing-git-directory\n");

        var result = await AuditAsync(new GitClient(), [broken, valid.Path], RuleCatalog.Create(RepoGleanConfig.Default));

        Assert.Equal(valid.Path, Assert.Single(result.Repositories).RepositoryRoot);
        Assert.Contains(result.Warnings, warning =>
            warning.Path == broken &&
            warning.Message.Contains("rev-parse", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Audit_uses_scan_repository_filters_and_exclusions()
    {
        using var temporary = new TemporaryDirectory();
        var included = await CreateIgnoredRepositoryAsync(temporary.GetPath("included"), "keep/data.bin", 7);
        included.WriteBytes("skip/data.bin", 13);
        included.Write(".gitignore", "keep/\nskip/\n");
        await included.CommitAllAsync("update ignores");
        included.WriteBytes("keep/data.bin", 7);
        included.WriteBytes("skip/data.bin", 13);
        var filteredOut = await CreateIgnoredRepositoryAsync(temporary.GetPath("filtered-out"), "other/data.bin", 17);
        var options = new AuditOptions(["INCLUDED"], ["s*"], 0);

        var result = await new RepositoryAuditor(new GitClient()).AuditAsync(
            [filteredOut.Path, included.Path],
            RuleCatalog.Create(RepoGleanConfig.Default),
            options);

        var repository = Assert.Single(result.Repositories);
        Assert.Equal(included.Path, repository.RepositoryRoot);
        var finding = Assert.Single(repository.Findings);
        Assert.Equal("keep", finding.RelativePath);
        Assert.Equal(7, finding.EstimatedBytes);
    }

    [Fact]
    public async Task Audit_observes_cancellation()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await GitTestRepository.CreateAsync(temporary.GetPath("repo"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new RepositoryAuditor(new GitClient()).AuditAsync(
                [repository.Path],
                RuleCatalog.Create(RepoGleanConfig.Default),
                cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task Audit_prunes_quarantines_nested_repositories_and_links_with_exact_path_warnings()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await GitTestRepository.CreateAsync(temporary.GetPath("repo"));
        repository.Write(".gitignore", "unknown/\n.repoglean-quarantine-*/\n");
        await repository.CommitAllAsync();
        repository.WriteBytes("unknown/local.bin", 5);
        var nested = await GitTestRepository.CreateAsync(repository.GetPath("unknown/nested"));
        nested.WriteBytes("payload.bin", 17);
        var quarantine = repository.GetPath(".repoglean-quarantine-0123456789abcdef");
        repository.WriteBytes(".repoglean-quarantine-0123456789abcdef/payload.bin", 19);
        var external = temporary.GetPath("external");
        Directory.CreateDirectory(external);
        File.WriteAllBytes(Path.Combine(external, "outside.bin"), new byte[23]);
        var link = repository.GetPath("unknown/link");
        var linkCreated = true;
        try
        {
            Directory.CreateSymbolicLink(link, external);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
        {
            linkCreated = false;
        }

        var result = await AuditAsync(new GitClient(), [repository.Path], RuleCatalog.Create(RepoGleanConfig.Default));

        var finding = Assert.Single(result.Repositories.Single().Findings);
        Assert.Equal("unknown", finding.RelativePath);
        Assert.Equal(1, finding.FileCount);
        Assert.Equal(5, finding.EstimatedBytes);
        Assert.Contains(
            new OperationWarning(
                quarantine,
                "Skipped reserved RepoGlean quarantine; inspect or remove the stranded payload manually."),
            result.Warnings);
        Assert.Contains(
            new OperationWarning(nested.Path, "Skipped nested repository boundary."),
            result.Warnings);
        if (linkCreated)
        {
            Assert.Contains(
                new OperationWarning(link, "Skipped audit filesystem link, junction, or reparse point."),
                result.Warnings);
        }
    }

    [Fact]
    public async Task Audit_prunes_an_injected_foreign_mount_without_discarding_siblings()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await GitTestRepository.CreateAsync(temporary.GetPath("repo"));
        repository.Write(".gitignore", "unknown/\n");
        await repository.CommitAllAsync();
        repository.WriteBytes("unknown/local.bin", 7);
        var foreign = repository.GetPath("unknown/foreign");
        repository.WriteBytes("unknown/foreign/data.bin", 29);
        var auditor = new RepositoryAuditor(
            new GitClient(),
            new StubVolumeBoundary(foreign),
            new StubTimestampProvider(),
            NullOperationProgress.Instance);

        var result = await auditor.AuditAsync(
            [repository.Path],
            RuleCatalog.Create(RepoGleanConfig.Default),
            new AuditOptions([], [], 0));

        var finding = Assert.Single(result.Repositories.Single().Findings);
        Assert.Equal(1, finding.FileCount);
        Assert.Equal(7, finding.EstimatedBytes);
        Assert.Contains(
            new OperationWarning(foreign, "Skipped path on a different filesystem mount or volume."),
            result.Warnings);
    }

    [Fact]
    public async Task Audit_preserves_spaces_unicode_tabs_and_platform_supported_newlines()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await GitTestRepository.CreateAsync(temporary.GetPath("repo"));
        repository.Write(".gitignore", "cache*/\n");
        await repository.CommitAllAsync();
        var expected = new List<(string Path, int Size)>
        {
            ("cache space", 1),
            ("cache-λ", 2),
            ("cache\ttab", 3),
        };
        if (!OperatingSystem.IsWindows()) expected.Add(("cache\nline", 4));
        foreach (var item in expected) repository.WriteBytes($"{item.Path}/payload.bin", item.Size);

        var result = await AuditAsync(new GitClient(), [repository.Path], RuleCatalog.Create(RepoGleanConfig.Default));

        var findings = result.Repositories.Single().Findings;
        Assert.Equal(expected.OrderByDescending(item => item.Size).Select(item => item.Path), findings.Select(item => item.RelativePath));
        Assert.Equal(expected.Sum(item => item.Size), result.EstimatedBytes);
        Assert.All(findings, finding => Assert.Equal("cache*/", finding.Ignore.Pattern));
    }

    [Fact]
    public async Task Audit_uses_the_newest_timestamp_from_the_finding_root_and_included_descendants()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await GitTestRepository.CreateAsync(temporary.GetPath("repo"));
        repository.Write(".gitignore", "unknown/\n");
        await repository.CommitAllAsync();
        repository.WriteBytes("unknown/one.bin", 5);
        repository.WriteBytes("unknown/nested/two.bin", 7);
        var expected = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
        var timestamps = new StubTimestampProvider(new Dictionary<string, DateTimeOffset?>
        {
            [repository.GetPath("unknown")] = expected.AddDays(-3),
            [repository.GetPath("unknown/one.bin")] = expected.AddDays(-2),
            [repository.GetPath("unknown/nested")] = expected,
            [repository.GetPath("unknown/nested/two.bin")] = expected.AddDays(-1),
        });
        var auditor = new RepositoryAuditor(
            new GitClient(),
            new StubVolumeBoundary(),
            timestamps,
            NullOperationProgress.Instance);

        var result = await auditor.AuditAsync(
            [repository.Path],
            RuleCatalog.Create(RepoGleanConfig.Default),
            new AuditOptions([], [], 0));

        Assert.Equal(expected, Assert.Single(result.Repositories.Single().Findings).NewestWriteTimeUtc);
    }

    [Fact]
    public async Task Audit_uses_null_timestamp_when_any_included_timestamp_is_unavailable()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await GitTestRepository.CreateAsync(temporary.GetPath("repo"));
        repository.Write(".gitignore", "unknown/\n");
        await repository.CommitAllAsync();
        repository.WriteBytes("unknown/one.bin", 5);
        var timestamps = new StubTimestampProvider(new Dictionary<string, DateTimeOffset?>
        {
            [repository.GetPath("unknown")] = DateTimeOffset.UnixEpoch,
            [repository.GetPath("unknown/one.bin")] = null,
        });
        var auditor = new RepositoryAuditor(
            new GitClient(),
            new StubVolumeBoundary(),
            timestamps,
            NullOperationProgress.Instance);

        var result = await auditor.AuditAsync(
            [repository.Path],
            RuleCatalog.Create(RepoGleanConfig.Default),
            new AuditOptions([], [], 0));

        Assert.Null(Assert.Single(result.Repositories.Single().Findings).NewestWriteTimeUtc);
        Assert.Equal(5, result.EstimatedBytes);
    }

    [Fact]
    public async Task Audit_timestamp_excludes_a_visible_only_branch_carved_from_the_finding()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await GitTestRepository.CreateAsync(temporary.GetPath("repo"));
        repository.Write(".gitignore", "unknown/\n");
        repository.Write("unknown/visible/tracked.bin", "tracked");
        await repository.GitAsync("add", "-f", "unknown/visible/tracked.bin");
        await repository.CommitAllAsync();
        repository.WriteBytes("unknown/counted.bin", 5);
        var expected = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
        var timestamps = new StubTimestampProvider(new Dictionary<string, DateTimeOffset?>
        {
            [repository.GetPath("unknown")] = expected.AddDays(-1),
            [repository.GetPath("unknown/counted.bin")] = expected,
            [repository.GetPath("unknown/visible")] = expected.AddDays(5),
        });
        var auditor = new RepositoryAuditor(
            new GitClient(),
            new StubVolumeBoundary(),
            timestamps,
            NullOperationProgress.Instance);

        var result = await auditor.AuditAsync(
            [repository.Path],
            RuleCatalog.Create(RepoGleanConfig.Default),
            new AuditOptions([], [], 0));

        Assert.Equal(expected, Assert.Single(result.Repositories.Single().Findings).NewestWriteTimeUtc);
    }

    [Fact]
    public async Task Audit_batches_129_sibling_classifications_into_two_git_calls()
    {
        if (OperatingSystem.IsWindows()) return;
        using var temporary = new TemporaryDirectory();
        var repository = await GitTestRepository.CreateAsync(temporary.GetPath("repo"));
        repository.Write(".gitignore", "artifact-*.bin\n");
        await repository.CommitAllAsync();
        const int siblingCount = 129;
        for (var index = 0; index < siblingCount; index++)
        {
            repository.WriteBytes($"artifact-{index:D3}.bin", 1);
        }

        var invocationLog = temporary.GetPath("check-ignore-invocations");
        var wrapper = temporary.GetPath("git-wrapper");
        File.WriteAllText(
            wrapper,
            "#!/bin/sh\nif [ \"$3\" = \"check-ignore\" ]; then printf 'check-ignore\\n' >> \"$REPOGLEAN_CHECK_IGNORE_LOG\"; fi\nexec git \"$@\"\n");
        File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var git = new GitClient(wrapper, new Dictionary<string, string?>
        {
            ["REPOGLEAN_CHECK_IGNORE_LOG"] = invocationLog,
        });

        var result = await AuditAsync(git, [repository.Path], RuleCatalog.Create(RepoGleanConfig.Default));

        Assert.Equal(siblingCount, result.Repositories.Single().Findings.Count);
        Assert.Equal(2, File.ReadAllLines(invocationLog).Length);
    }

    [Fact]
    public async Task Audit_isolates_a_single_path_git_failure_without_suppressing_its_sibling()
    {
        if (OperatingSystem.IsWindows()) return;
        using var temporary = new TemporaryDirectory();
        var repository = await GitTestRepository.CreateAsync(temporary.GetPath("repo"));
        repository.Write(".gitignore", "*.bin\n");
        await repository.CommitAllAsync();
        repository.WriteBytes("broken.bin", 13);
        repository.WriteBytes("good.bin", 17);
        var input = temporary.GetPath("check-ignore-input");
        var wrapper = temporary.GetPath("git-wrapper");
        File.WriteAllText(
            wrapper,
            "#!/bin/sh\nif [ \"$3\" = \"check-ignore\" ]; then cat > \"$REPOGLEAN_CHECK_IGNORE_INPUT\"; if tr '\\0' '\\n' < \"$REPOGLEAN_CHECK_IGNORE_INPUT\" | grep -Fxq 'broken.bin'; then echo injected-check-ignore-failure >&2; exit 2; fi; exec git \"$@\" < \"$REPOGLEAN_CHECK_IGNORE_INPUT\"; fi\nexec git \"$@\"\n");
        File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var git = new GitClient(wrapper, new Dictionary<string, string?>
        {
            ["REPOGLEAN_CHECK_IGNORE_INPUT"] = input,
        });

        var result = await AuditAsync(git, [repository.Path], RuleCatalog.Create(RepoGleanConfig.Default));

        var finding = Assert.Single(result.Repositories.Single().Findings);
        Assert.Equal("good.bin", finding.RelativePath);
        Assert.Equal(17, finding.EstimatedBytes);
        Assert.Contains(result.Warnings, warning =>
            warning.Path == repository.GetPath("broken.bin") &&
            warning.Message.Contains("injected-check-ignore-failure", StringComparison.Ordinal));
    }

    private static Task<AuditResult> AuditAsync(
        GitClient git,
        IReadOnlyList<string> repositoryRoots,
        RuleCatalog catalog) =>
        new RepositoryAuditor(git).AuditAsync(repositoryRoots, catalog, new AuditOptions([], [], 0));

    private static async Task<GitTestRepository> CreateIgnoredRepositoryAsync(
        string path,
        string ignoredRelativePath,
        int size)
    {
        var repository = await GitTestRepository.CreateAsync(path);
        var ignoredRoot = ignoredRelativePath.Split('/')[0];
        repository.Write(".gitignore", $"{ignoredRoot}/\n");
        await repository.CommitAllAsync();
        repository.WriteBytes(ignoredRelativePath, size);
        return repository;
    }

    private sealed class StubVolumeBoundary(string? foreignRoot = null) : IVolumeBoundary
    {
        public bool TryGetMountIdentity(string path, out FileSystemMountIdentity? identity, out string? error)
        {
            var foreign = foreignRoot is not null &&
                RepositoryDiscovery.IsSameOrDescendant(Path.GetFullPath(path), Path.GetFullPath(foreignRoot));
            identity = foreign
                ? new FileSystemMountIdentity(2, "foreign")
                : new FileSystemMountIdentity(1, "repository");
            error = null;
            return true;
        }
    }

    private sealed class StubTimestampProvider(
        IReadOnlyDictionary<string, DateTimeOffset?>? timestamps = null) : IFileTimestampProvider
    {
        public bool TryGetLastWriteTimeUtc(string path, out DateTimeOffset value)
        {
            if (timestamps is null || !timestamps.TryGetValue(path, out var configured))
            {
                value = DateTimeOffset.UnixEpoch;
                return true;
            }

            value = configured.GetValueOrDefault();
            return configured.HasValue;
        }
    }
}
