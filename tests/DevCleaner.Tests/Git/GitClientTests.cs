using DevCleaner.Git;
using DevCleaner.Tests.Support;

namespace DevCleaner.Tests.Git;

public sealed class GitClientTests
{
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
