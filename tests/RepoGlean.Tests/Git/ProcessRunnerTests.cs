using System.Text;
using RepoGlean.Git;

namespace RepoGlean.Tests.Git;

public sealed class ProcessRunnerTests
{
    [Fact]
    public void Git_processes_use_utf8_for_nul_delimited_path_input_and_output()
    {
        var startInfo = new ProcessRunner("git").CreateStartInfo(
            ["check-ignore", "--stdin", "-z"],
            workingDirectory: null,
            redirectStandardInput: true);

        Assert.Equal(Encoding.UTF8.WebName, startInfo.StandardInputEncoding?.WebName);
        Assert.Empty(startInfo.StandardInputEncoding!.GetPreamble());
        Assert.Equal(Encoding.UTF8.WebName, startInfo.StandardOutputEncoding?.WebName);
        Assert.Equal(Encoding.UTF8.WebName, startInfo.StandardErrorEncoding?.WebName);
    }
}
