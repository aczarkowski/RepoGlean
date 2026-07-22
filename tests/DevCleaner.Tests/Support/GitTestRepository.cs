using System.Diagnostics;

namespace DevCleaner.Tests.Support;

internal sealed class GitTestRepository
{
    private GitTestRepository(string path)
    {
        Path = path;
    }

    public string Path { get; }

    public static async Task<GitTestRepository> CreateAsync(string path)
    {
        Directory.CreateDirectory(path);
        var repository = new GitTestRepository(path);
        await repository.GitAsync("init", "--quiet");
        await repository.GitAsync("config", "user.name", "DevCleaner Tests");
        await repository.GitAsync("config", "user.email", "devcleaner@example.invalid");
        return repository;
    }

    public void Write(string relativePath, string contents)
    {
        var fullPath = GetPath(relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, contents);
    }

    public void WriteBytes(string relativePath, int count)
    {
        var fullPath = GetPath(relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fullPath)!);
        File.WriteAllBytes(fullPath, new byte[count]);
    }

    public string GetPath(string relativePath) => System.IO.Path.Combine(Path, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));

    public async Task CommitAllAsync(string message = "test")
    {
        await GitAsync("add", "-A");
        await GitAsync("commit", "--quiet", "-m", message);
    }

    public Task<string> GitAsync(params string[] arguments) => RunAsync("git", Path, null, arguments);

    public static async Task<string> RunAsync(
        string executable,
        string workingDirectory,
        IReadOnlyDictionary<string, string?>? environment,
        params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        if (environment is not null)
        {
            foreach (var pair in environment) startInfo.Environment[pair.Key] = pair.Value;
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start {executable}.");
        var standardOutput = await process.StandardOutput.ReadToEndAsync();
        var standardError = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{executable} {string.Join(' ', arguments)} failed ({process.ExitCode}): {standardError}");
        }

        return standardOutput;
    }
}
