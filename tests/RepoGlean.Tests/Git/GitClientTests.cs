using RepoGlean.Git;
using RepoGlean.Tests.Support;

namespace RepoGlean.Tests.Git;

public sealed class GitClientTests
{
    [Fact]
    public async Task Verbose_ignore_matches_preserve_source_line_pattern_and_path()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await GitTestRepository.CreateAsync(temporary.GetPath("repo"));
        repository.Write(".gitignore", "visible/\ncache*/\n");
        repository.Write("visible/file.bin", "tracked");
        await repository.GitAsync("add", ".gitignore");
        await repository.GitAsync("add", "-f", "visible/file.bin");
        await repository.GitAsync("commit", "--quiet", "-m", "tracked visible path");
        repository.Write("cache space/payload.bin", "ignored");
        var paths = new List<string> { "cache space", "visible/file.bin" };
        if (!OperatingSystem.IsWindows())
        {
            repository.Write("cache\nline/payload.bin", "ignored");
            paths.Add("cache\nline");
        }

        var matches = await new GitClient().GetIgnoreMatchesAsync(repository.Path, paths);

        var ignored = matches["cache space"];
        Assert.True(ignored.IsIgnored);
        Assert.Equal(".gitignore", ignored.Source);
        Assert.Equal(2, ignored.SourceLine);
        Assert.Equal("cache*/", ignored.Pattern);
        Assert.Equal("cache space", ignored.Path);
        Assert.False(matches["visible/file.bin"].IsIgnored);
        if (!OperatingSystem.IsWindows()) Assert.True(matches["cache\nline"].IsIgnored);
    }

    [Fact]
    public async Task Verbose_ignore_matches_report_repository_info_exclude_provenance()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await GitTestRepository.CreateAsync(temporary.GetPath("repo"));
        repository.Write("project.csproj", "<Project />");
        await repository.CommitAllAsync();
        repository.Write(".git/info/exclude", "scratch/\n");
        repository.Write("scratch/payload.bin", "ignored");

        var match = (await new GitClient().GetIgnoreMatchesAsync(repository.Path, ["scratch"]))["scratch"];

        Assert.True(match.IsIgnored);
        Assert.Equal(".git/info/exclude", match.Source);
        Assert.Equal(1, match.SourceLine);
        Assert.Equal("scratch/", match.Pattern);
    }

    [Fact]
    public async Task Verbose_ignore_matches_report_absolute_global_excludes_provenance()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await GitTestRepository.CreateAsync(temporary.GetPath("repo"));
        repository.Write("project.csproj", "<Project />");
        await repository.CommitAllAsync();
        var globalIgnore = temporary.GetPath("global-ignore");
        File.WriteAllText(globalIgnore, "global-cache/\n");
        var globalConfig = temporary.GetPath("global-config");
        await GitTestRepository.RunAsync("git", temporary.Path, new Dictionary<string, string?> { ["GIT_CONFIG_GLOBAL"] = globalConfig },
            "config", "--global", "core.excludesFile", globalIgnore);
        repository.Write("global-cache/payload.bin", "ignored");

        var git = new GitClient(environment: new Dictionary<string, string?> { ["GIT_CONFIG_GLOBAL"] = globalConfig });
        var match = (await git.GetIgnoreMatchesAsync(repository.Path, ["global-cache"]))["global-cache"];

        Assert.True(match.IsIgnored);
        Assert.Equal(Path.GetFullPath(globalIgnore), match.Source);
        Assert.Equal(1, match.SourceLine);
        Assert.Equal("global-cache/", match.Pattern);
    }

    [Fact]
    public async Task Verbose_ignore_matches_preserve_a_negated_pattern_for_a_visible_path()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await GitTestRepository.CreateAsync(temporary.GetPath("repo"));
        repository.Write(".gitignore", "*\n!keep/\n");
        await repository.GitAsync("add", "-f", ".gitignore");
        await repository.GitAsync("commit", "--quiet", "-m", "ignore rules");
        repository.Write("keep/payload.bin", "visible");

        var match = (await new GitClient().GetIgnoreMatchesAsync(repository.Path, ["keep"]))["keep"];

        Assert.False(match.IsIgnored);
        Assert.Equal(".gitignore", match.Source);
        Assert.Equal(2, match.SourceLine);
        Assert.Equal("!keep/", match.Pattern);
    }

    [Fact]
    public async Task Visible_file_evaluation_always_excludes_the_reserved_repository_local_quarantine_namespace()
    {
        Assert.Equal(".repoglean-quarantine-", GitClient.QuarantineDirectoryPrefix);

        using var temporary = new TemporaryDirectory();
        var repository = await GitTestRepository.CreateAsync(temporary.GetPath("repo"));
        repository.Write(".gitignore", "obj/\n");
        repository.Write("project.csproj", "<Project />");
        await repository.CommitAllAsync();
        repository.Write("obj/quarantined-marker.csproj", "<Project />");
        var quarantineRelativePath = ".repoglean-quarantine-0123456789abcdef";
        var quarantinePath = repository.GetPath(quarantineRelativePath);
        Directory.CreateDirectory(quarantinePath);
        Directory.Move(repository.GetPath("obj"), Path.Combine(quarantinePath, "payload"));
        var git = new GitClient();

        var reservedNamespaceFiltered = await git.ListVisibleFilesAsync(repository.Path);
        var filtered = await git.ListVisibleFilesExcludingAsync(repository.Path, quarantineRelativePath);

        Assert.Contains("project.csproj", reservedNamespaceFiltered);
        Assert.DoesNotContain(reservedNamespaceFiltered, path => path.StartsWith(quarantineRelativePath, StringComparison.Ordinal));
        Assert.Contains("project.csproj", filtered);
        Assert.DoesNotContain(filtered, path => path.StartsWith(quarantineRelativePath, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Ignore_authority_can_be_evaluated_for_an_absent_path_without_index_suppression()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await GitTestRepository.CreateAsync(temporary.GetPath("repo"));
        repository.Write(".gitignore", "obj/\n");
        repository.Write("obj/tracked.bin", "tracked");
        await repository.GitAsync("add", ".gitignore");
        await repository.GitAsync("add", "-f", "obj/tracked.bin");
        await repository.GitAsync("commit", "--quiet", "-m", "tracked ignored path");
        var git = new GitClient();

        var ordinary = await git.IsIgnoredAsync(repository.Path, "obj/");
        var withoutIndex = await git.IsIgnoredWithoutIndexAsync(repository.Path, "obj/");

        Assert.False(ordinary);
        Assert.True(withoutIndex);
    }
}
