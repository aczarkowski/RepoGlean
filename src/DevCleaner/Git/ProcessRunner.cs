using System.ComponentModel;
using System.Diagnostics;

namespace DevCleaner.Git;

internal sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

internal sealed class ProcessRunner
{
    private readonly string executable;
    private readonly IReadOnlyDictionary<string, string?> environment;

    public ProcessRunner(string executable, IReadOnlyDictionary<string, string?>? environment = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        this.executable = executable;
        this.environment = environment is null
            ? new Dictionary<string, string?>()
            : new Dictionary<string, string?>(environment, StringComparer.Ordinal);
    }

    public async Task<ProcessResult> RunAsync(
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        CancellationToken cancellationToken,
        string? standardInput = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = standardInput is not null,
            CreateNoWindow = true,
        };
        if (!string.IsNullOrWhiteSpace(workingDirectory)) startInfo.WorkingDirectory = workingDirectory;
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        foreach (var pair in environment) startInfo.Environment[pair.Key] = pair.Value;

        Process process;
        try
        {
            process = Process.Start(startInfo) ?? throw new GitUnavailableException($"Git executable '{executable}' could not be started.");
        }
        catch (Exception exception) when (exception is Win32Exception or FileNotFoundException)
        {
            throw new GitUnavailableException($"Git executable '{executable}' was not found or could not be started.", exception);
        }

        using (process)
        {
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            try
            {
                if (standardInput is not null)
                {
                    await process.StandardInput.WriteAsync(standardInput.AsMemory(), cancellationToken).ConfigureAwait(false);
                    await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
                    process.StandardInput.Close();
                }

                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                return new ProcessResult(
                    process.ExitCode,
                    await outputTask.ConfigureAwait(false),
                    await errorTask.ConfigureAwait(false));
            }
            catch (OperationCanceledException)
            {
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }
                catch (Win32Exception)
                {
                }

                try
                {
                    await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (InvalidOperationException)
                {
                }

                throw;
            }
        }
    }
}
