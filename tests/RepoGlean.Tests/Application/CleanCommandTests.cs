using System.Text.Json;
using RepoGlean.Tests.Support;

namespace RepoGlean.Tests.Application;

public sealed class CleanCommandTests
{
    [Fact]
    public async Task Interactive_defaults_select_all_repositories_but_only_preselected_artifacts()
    {
        using var temporary = new TemporaryDirectory();
        var first = await CreateRepositoryAsync(temporary.GetPath("first"));
        var second = await CreateRepositoryAsync(temporary.GetPath("second"));

        var result = await RunAsync(["clean", temporary.Path], "\n\ndelete\n");

        Assert.Equal(0, result.ExitCode);
        Assert.False(Directory.Exists(first.GetPath("obj")));
        Assert.False(Directory.Exists(second.GetPath("obj")));
        Assert.True(Directory.Exists(first.GetPath("node_modules")));
        Assert.True(Directory.Exists(second.GetPath("node_modules")));
    }

    [Fact]
    public async Task Interactive_all_is_the_explicit_dependency_opt_in()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await CreateRepositoryAsync(temporary.GetPath("repo"));

        var result = await RunAsync(["clean", repository.Path], "\nall\ndelete\n");

        Assert.Equal(0, result.ExitCode);
        Assert.False(Directory.Exists(repository.GetPath("obj")));
        Assert.False(Directory.Exists(repository.GetPath("node_modules")));
    }

    [Fact]
    public async Task Interactive_all_flag_makes_enter_include_dependencies()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await CreateRepositoryAsync(temporary.GetPath("repo"));

        var result = await RunAsync(["clean", repository.Path, "--all"], "\n\ndelete\n");

        Assert.Equal(0, result.ExitCode);
        Assert.False(Directory.Exists(repository.GetPath("obj")));
        Assert.False(Directory.Exists(repository.GetPath("node_modules")));
    }

    [Fact]
    public async Task Interactive_dependency_category_makes_enter_select_matching_dependencies()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await CreateRepositoryAsync(temporary.GetPath("repo"));

        var result = await RunAsync(["clean", repository.Path, "--category", "dependency"], "\n\ndelete\n");

        Assert.Equal(0, result.ExitCode);
        Assert.True(Directory.Exists(repository.GetPath("obj")));
        Assert.False(Directory.Exists(repository.GetPath("node_modules")));
    }

    [Theory]
    [InlineData("DELETE")]
    [InlineData("no")]
    [InlineData("")]
    public async Task Interactive_confirmation_requires_literal_lowercase_delete(string confirmation)
    {
        using var temporary = new TemporaryDirectory();
        var repository = await CreateRepositoryAsync(temporary.GetPath("repo"));

        var result = await RunAsync(["clean", repository.Path], $"\n\n{confirmation}\n");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("cancel", result.Stdout, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(repository.GetPath("obj")));
    }

    [Fact]
    public async Task Dry_run_is_noninteractive_and_preserves_all_selected_content()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await CreateRepositoryAsync(temporary.GetPath("repo"));

        var result = await RunAsync(["clean", repository.Path, "--dry-run", "--all"], string.Empty);

        Assert.Equal(0, result.ExitCode);
        Assert.True(Directory.Exists(repository.GetPath("obj")));
        Assert.True(Directory.Exists(repository.GetPath("node_modules")));
        Assert.Contains("dry run", result.Stdout, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Unattended_repository_scope_uses_default_preselection()
    {
        using var temporary = new TemporaryDirectory();
        var first = await CreateRepositoryAsync(temporary.GetPath("first"));
        var second = await CreateRepositoryAsync(temporary.GetPath("second"));

        var result = await RunAsync(["clean", temporary.Path, "--yes", "--repo", "first"], string.Empty);

        Assert.Equal(0, result.ExitCode);
        Assert.False(Directory.Exists(first.GetPath("obj")));
        Assert.True(Directory.Exists(first.GetPath("node_modules")));
        Assert.True(Directory.Exists(second.GetPath("obj")));
    }

    [Fact]
    public async Task Unattended_category_and_all_filters_opt_in_dependencies()
    {
        using var temporary = new TemporaryDirectory();
        var categoryRepository = await CreateRepositoryAsync(temporary.GetPath("category"));
        var allRepository = await CreateRepositoryAsync(temporary.GetPath("all"));

        var category = await RunAsync(["clean", categoryRepository.Path, "--yes", "--category", "dependency"], string.Empty);
        var all = await RunAsync(["clean", allRepository.Path, "--yes", "--all"], string.Empty);

        Assert.Equal(0, category.ExitCode);
        Assert.True(Directory.Exists(categoryRepository.GetPath("obj")));
        Assert.False(Directory.Exists(categoryRepository.GetPath("node_modules")));
        Assert.Equal(0, all.ExitCode);
        Assert.False(Directory.Exists(allRepository.GetPath("obj")));
        Assert.False(Directory.Exists(allRepository.GetPath("node_modules")));
    }

    [Fact]
    public async Task Json_cleanup_is_machine_clean_and_reports_exact_outcomes()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await CreateRepositoryAsync(temporary.GetPath("repo"));

        var result = await RunAsync(["clean", repository.Path, "--yes", "--all", "--format", "json"], string.Empty, isErrorInteractive: true);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.Stderr);
        using var document = JsonDocument.Parse(result.Stdout);
        var root = document.RootElement;
        Assert.Equal("clean", root.GetProperty("operation").GetString());
        Assert.Equal("success", root.GetProperty("status").GetString());
        Assert.Equal(2, root.GetProperty("cleanup").GetProperty("deletedCount").GetInt64());
        Assert.All(
            root.GetProperty("repositories").EnumerateArray().SelectMany(repositoryElement => repositoryElement.GetProperty("candidates").EnumerateArray()),
            candidate => Assert.Equal("deleted", candidate.GetProperty("outcome").GetString()));
    }

    [Fact]
    public async Task Human_partial_cleanup_uses_scan_style_quiet_and_verbose_diagnostics()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await CreateRepositoryAsync(temporary.GetPath("repo"));
        var missingRoot = temporary.GetPath("missing-root");

        var standard = await RunAsync(["clean", repository.Path, missingRoot, "--dry-run"], string.Empty);
        var verbose = await RunAsync(["clean", repository.Path, missingRoot, "--dry-run", "--verbose"], string.Empty);
        var quiet = await RunAsync(["clean", repository.Path, missingRoot, "--dry-run", "--quiet"], string.Empty);

        Assert.Equal(3, standard.ExitCode);
        Assert.Contains("Warnings: 1", standard.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(missingRoot, standard.Stdout, StringComparison.Ordinal);
        Assert.Equal(3, verbose.ExitCode);
        Assert.Contains("Warnings: 1", verbose.Stdout, StringComparison.Ordinal);
        Assert.Contains(missingRoot, verbose.Stdout, StringComparison.Ordinal);
        Assert.Equal(3, quiet.ExitCode);
        Assert.Contains("Dry run:", quiet.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("Warnings:", quiet.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(missingRoot, quiet.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Interactive_cleanup_clears_progress_before_prompts_and_resumes_only_after_confirmation()
    {
        using var temporary = new TemporaryDirectory();
        var confirmedRepository = await CreateRepositoryAsync(temporary.GetPath("confirmed"));
        var confirmed = await RunOrderedAsync(
            ["clean", confirmedRepository.Path],
            "\n\ndelete\n");

        Assert.Equal(0, confirmed.Result.ExitCode);
        AssertProgressIsClearBefore(confirmed.Writes, "Repositories:");
        AssertProgressIsClearBefore(confirmed.Writes, "Artifacts:");
        var confirmationIndex = AssertProgressIsClearBefore(confirmed.Writes, "Type delete");
        Assert.Contains(
            confirmed.Writes.Skip(confirmationIndex + 1),
            write => write.Stream == "stderr" &&
                     write.Text.StartsWith('\r') &&
                     !IsClearWrite(write.Text));

        var cancelledRepository = await CreateRepositoryAsync(temporary.GetPath("cancelled"));
        var cancelled = await RunOrderedAsync(
            ["clean", cancelledRepository.Path],
            "\n\nno\n");

        Assert.Equal(0, cancelled.Result.ExitCode);
        var cancelledConfirmationIndex = AssertProgressIsClearBefore(cancelled.Writes, "Type delete");
        Assert.DoesNotContain(
            cancelled.Writes.Skip(cancelledConfirmationIndex + 1),
            write => write.Stream == "stderr" && !IsClearWrite(write.Text));
        Assert.Contains("Cleanup cancelled; nothing was deleted.", cancelled.Result.Stdout, StringComparison.Ordinal);
        Assert.True(Directory.Exists(cancelledRepository.GetPath("obj")));
    }

    [Fact]
    public async Task Verbose_dry_run_uses_validation_terms_without_claiming_deletion()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await CreateRepositoryAsync(temporary.GetPath("repo"));

        var result = await RunAsync(
            ["clean", repository.Path, "--dry-run", "--all", "--verbose"],
            string.Empty);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Validating [1/2]", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("Validated repo/obj", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("Validated repo/node_modules", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("Deleted", result.Stderr, StringComparison.Ordinal);
        Assert.Contains(
            "Cleanup complete: 0 deleted, 2 validated, 0 skipped, 0 failed, 0 warnings.",
            result.Stderr,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Verbose_cleanup_reports_a_safety_skip_from_the_authoritative_result()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await CreateRepositoryAsync(temporary.GetPath("repo"));

        var result = await RunAsync(
            ["clean", repository.Path, "--verbose"],
            "\n\ndelete\n",
            beforeReadLine: lineNumber =>
            {
                if (lineNumber != 3) return;
                Directory.Delete(repository.GetPath("obj"), recursive: true);
                Directory.CreateDirectory(repository.GetPath("obj"));
                File.WriteAllText(repository.GetPath("obj/replacement.bin"), "replacement");
            });

        Assert.Equal(3, result.ExitCode);
        Assert.Contains("Skipped repo/obj.", result.Stderr, StringComparison.Ordinal);
        Assert.Contains(
            "Cleanup complete: 0 deleted, 0 validated, 1 skipped, 0 failed, 1 warning.",
            result.Stderr,
            StringComparison.Ordinal);
        Assert.Contains("Cleanup: 0 deleted, 1 skipped, 0 failed", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Verbose_cleanup_reports_a_candidate_failure_from_the_authoritative_result()
    {
        if (OperatingSystem.IsWindows()) return;

        using var temporary = new TemporaryDirectory();
        var repository = await CreateRepositoryAsync(temporary.GetPath("repo"));
        var markerPath = temporary.GetPath("fail-cleanup");
        var gitWrapper = CreateCleanupFailureGitWrapper(
            temporary.GetPath("git-wrapper"),
            markerPath,
            repository.Path);

        var result = await RunAsync(
            ["clean", repository.Path, "--verbose"],
            "\n\ndelete\n",
            gitExecutable: gitWrapper,
            beforeReadLine: lineNumber =>
            {
                if (lineNumber == 3) File.WriteAllText(markerPath, string.Empty);
            });

        Assert.Equal(3, result.ExitCode);
        Assert.Contains("Failed repo/obj.", result.Stderr, StringComparison.Ordinal);
        Assert.Contains(
            "Cleanup complete: 0 deleted, 0 validated, 0 skipped, 1 failed, 0 warnings.",
            result.Stderr,
            StringComparison.Ordinal);
        Assert.Contains(
            "Cleanup: 0 deleted, 0 skipped, 1 failed | 1 selected, 1 processed",
            result.Stdout,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Verbose_interrupted_cleanup_uses_processed_selected_and_outcome_totals()
    {
        using var temporary = new TemporaryDirectory();
        var repository = await CreateRepositoryAsync(temporary.GetPath("repo"));
        using var cancellation = new CancellationTokenSource();

        var result = await RunAsync(
            ["clean", repository.Path, "--verbose"],
            "\n\ndelete\n",
            beforeReadLine: lineNumber =>
            {
                if (lineNumber == 3) cancellation.Cancel();
            },
            cancellationToken: cancellation.Token);

        Assert.Equal(130, result.ExitCode);
        Assert.Contains(
            "Cleanup interrupted: 0 deleted, 0 validated, 0 skipped, 0 failed, 0 warnings.",
            result.Stderr,
            StringComparison.Ordinal);
        Assert.Contains(
            "Cleanup: 0 deleted, 0 skipped, 0 failed | 1 selected, 0 processed",
            result.Stdout,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Cleanup complete:", result.Stderr, StringComparison.Ordinal);
        Assert.True(Directory.Exists(repository.GetPath("obj")));
    }

    private static async Task<GitTestRepository> CreateRepositoryAsync(string path)
    {
        var repository = await GitTestRepository.CreateAsync(path);
        repository.Write("project.csproj", "<Project />");
        repository.Write("package.json", "{}");
        repository.Write(".gitignore", "obj/\nnode_modules/\n");
        repository.WriteBytes("obj/artifact.bin", 5);
        repository.WriteBytes("node_modules/package.bin", 7);
        await repository.CommitAllAsync();
        return repository;
    }

    private static async Task<AppResult> RunAsync(
        string[] arguments,
        string inputText,
        bool isErrorInteractive = false,
        string gitExecutable = "git",
        Action<int>? beforeReadLine = null,
        CancellationToken cancellationToken = default)
    {
        using var input = beforeReadLine is null
            ? new StringReader(inputText)
            : new CallbackTextReader(inputText, beforeReadLine);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var runtime = new AppRuntime(gitExecutable, Path.GetTempPath(), isErrorInteractive);
        var exitCode = await RepoGleanApp.RunAsync(arguments, input, stdout, stderr, runtime, cancellationToken);
        return new AppResult(exitCode, stdout.ToString(), stderr.ToString());
    }

    private static async Task<OrderedAppResult> RunOrderedAsync(string[] arguments, string inputText)
    {
        using var input = new StringReader(inputText);
        var ledger = new WriteLedger();
        using var stdout = new OrderedTextWriter("stdout", ledger);
        using var stderr = new OrderedTextWriter("stderr", ledger);
        var runtime = new AppRuntime("git", Path.GetTempPath(), IsErrorInteractive: true);
        var exitCode = await RepoGleanApp.RunAsync(
            arguments,
            input,
            stdout,
            stderr,
            runtime,
            CancellationToken.None);
        return new OrderedAppResult(
            new AppResult(exitCode, stdout.ToString(), stderr.ToString()),
            ledger.Snapshot());
    }

    private static int AssertProgressIsClearBefore(IReadOnlyList<OrderedWrite> writes, string stdoutText)
    {
        var outputIndex = writes
            .Select((write, index) => (write, index))
            .First(item =>
                item.write.Stream == "stdout" &&
                item.write.Text.Contains(stdoutText, StringComparison.Ordinal))
            .index;
        var lastError = writes
            .Take(outputIndex)
            .Last(write => write.Stream == "stderr");
        Assert.True(
            IsClearWrite(lastError.Text),
            $"Expected progress to be clear before '{stdoutText}', but the last stderr write was '{lastError.Text}'.");
        return outputIndex;
    }

    private static bool IsClearWrite(string text) =>
        text.StartsWith('\r') &&
        text.Length > 1 &&
        text.All(character => character is '\r' or ' ');

    private static string CreateCleanupFailureGitWrapper(
        string path,
        string markerPath,
        string repositoryPath)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();

        static string Quote(string value) => $"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";

        File.WriteAllText(
            path,
            $"""
            #!/bin/sh
            has_check_ignore=false
            has_quiet=false
            for argument in "$@"; do
              if [ "$argument" = "check-ignore" ]; then has_check_ignore=true; fi
              if [ "$argument" = "-q" ]; then has_quiet=true; fi
            done
            if [ -f {Quote(markerPath)} ] && [ "$has_check_ignore" = true ] && [ "$has_quiet" = true ]; then
              git "$@"
              status=$?
              if [ "$status" -eq 0 ]; then rm -rf {Quote(repositoryPath)}; fi
              exit "$status"
            fi
            exec git "$@"
            """);
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead |
            UnixFileMode.UserWrite |
            UnixFileMode.UserExecute);
        return path;
    }

    private sealed record AppResult(int ExitCode, string Stdout, string Stderr);

    private sealed record OrderedAppResult(AppResult Result, IReadOnlyList<OrderedWrite> Writes);

    private sealed record OrderedWrite(int Sequence, string Stream, string Text);

    private sealed class CallbackTextReader(string text, Action<int> beforeReadLine) : StringReader(text)
    {
        private int lineNumber;

        public override ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lineNumber++;
            beforeReadLine(lineNumber);
            return ValueTask.FromResult(ReadLine());
        }
    }

    private sealed class WriteLedger
    {
        private readonly object sync = new();
        private readonly List<OrderedWrite> writes = [];
        private int sequence;

        public void Add(string stream, string text)
        {
            lock (sync)
            {
                writes.Add(new OrderedWrite(++sequence, stream, text));
            }
        }

        public IReadOnlyList<OrderedWrite> Snapshot()
        {
            lock (sync)
            {
                return writes.OrderBy(write => write.Sequence).ToArray();
            }
        }
    }

    private sealed class OrderedTextWriter(string stream, WriteLedger ledger) : StringWriter
    {
        public override void Write(char value) => Append(value.ToString());

        public override void Write(string? value) => Append(value ?? string.Empty);

        public override void Write(ReadOnlySpan<char> buffer) => Append(buffer.ToString());

        public override void WriteLine() => Append(NewLine);

        public override void WriteLine(string? value) => Append($"{value}{NewLine}");

        public override Task WriteAsync(string? value)
        {
            Append(value ?? string.Empty);
            return Task.CompletedTask;
        }

        public override Task WriteAsync(
            ReadOnlyMemory<char> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Append(buffer.ToString());
            return Task.CompletedTask;
        }

        public override Task WriteLineAsync(string? value)
        {
            Append($"{value}{NewLine}");
            return Task.CompletedTask;
        }

        public override Task WriteLineAsync(
            ReadOnlyMemory<char> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Append($"{buffer}{NewLine}");
            return Task.CompletedTask;
        }

        private void Append(string value)
        {
            ledger.Add(stream, value);
            GetStringBuilder().Append(value);
        }
    }
}
