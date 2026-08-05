using System.Text.Json;
using RepoGlean.Auditing;
using RepoGlean.Git;
using RepoGlean.Output;
using RepoGlean.Scanning;

namespace RepoGlean.Tests.Output;

public sealed class AuditReportTests
{
    [Fact]
    public async Task Audit_json_has_a_dedicated_versioned_shape_and_preserves_null_evidence()
    {
        var fixture = CreateFixture();
        using var output = new StringWriter();

        await JsonReportWriter.WriteAsync(fixture.Report, output);

        using var document = JsonDocument.Parse(output.ToString());
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("audit", root.GetProperty("operation").GetString());
        Assert.Equal("partial", root.GetProperty("status").GetString());
        Assert.Equal(3, root.GetProperty("totals").GetProperty("findingCount").GetInt64());

        var firstRepository = root.GetProperty("repositories")[0];
        Assert.Equal(fixture.LargeRepository, firstRepository.GetProperty("root").GetString());
        Assert.Equal("same-a", firstRepository.GetProperty("findings")[0].GetProperty("relativePath").GetString());
        Assert.Equal("same-b", firstRepository.GetProperty("findings")[1].GetProperty("relativePath").GetString());
        Assert.Equal(JsonValueKind.Number, firstRepository.GetProperty("findings")[0].GetProperty("estimatedBytes").ValueKind);
        Assert.Equal(JsonValueKind.Null, firstRepository.GetProperty("findings")[0].GetProperty("newestWriteTimeUtc").ValueKind);
        Assert.Equal(JsonValueKind.Null, firstRepository.GetProperty("findings")[0].GetProperty("ignoreSource").ValueKind);
        Assert.Equal(JsonValueKind.Null, firstRepository.GetProperty("findings")[0].GetProperty("ignoreSourceLine").ValueKind);
        Assert.Equal(JsonValueKind.Null, firstRepository.GetProperty("findings")[0].GetProperty("ignorePattern").ValueKind);

        var smallFinding = root.GetProperty("repositories")[1].GetProperty("findings")[0];
        Assert.Equal(".gitignore", smallFinding.GetProperty("ignoreSource").GetString());
        Assert.Equal(42, smallFinding.GetProperty("ignoreSourceLine").GetInt32());
        Assert.Equal("/unknown/", smallFinding.GetProperty("ignorePattern").GetString());
    }

    [Fact]
    public void Audit_report_normalizes_external_ignore_sources_to_an_absolute_path()
    {
        var fixture = CreateFixture();
        var externalSource = Path.Combine(Path.GetTempPath(), "repoglean-audit-global-ignore");
        var result = new AuditResult(
            [
                new RepositoryAuditResult(
                    fixture.SmallRepository,
                    [new AuditFinding(
                        fixture.SmallRepository,
                        Path.Combine(fixture.SmallRepository, "unknown"),
                        "unknown",
                        1,
                        1,
                        null,
                        new GitIgnoreMatch("unknown", externalSource, null, "unknown/"))],
                    1,
                    1,
                    []),
            ],
            1,
            1,
            []);

        var report = AuditReportDocument.FromAudit([fixture.Root], result);

        Assert.Equal(Path.GetFullPath(externalSource), report.Repositories[0].Findings[0].IgnoreSource);
    }

    private static AuditFixture CreateFixture()
    {
        var root = Path.Combine(Path.GetTempPath(), "repoglean-audit-report-root");
        var largeRepository = Path.Combine(root, "large");
        var smallRepository = Path.Combine(root, "small");
        var result = new AuditResult(
            [
                new RepositoryAuditResult(
                    smallRepository,
                    [new AuditFinding(
                        smallRepository,
                        Path.Combine(smallRepository, "unknown"),
                        "unknown",
                        1,
                        100,
                        DateTimeOffset.UnixEpoch,
                        new GitIgnoreMatch("unknown", Path.Combine(smallRepository, ".gitignore"), 42, "/unknown/"))],
                    1,
                    100,
                    []),
                new RepositoryAuditResult(
                    largeRepository,
                    [
                        new AuditFinding(
                            largeRepository,
                            Path.Combine(largeRepository, "same-b"),
                            "same-b",
                            2,
                            200,
                            null,
                            new GitIgnoreMatch("same-b", null, null, null)),
                        new AuditFinding(
                            largeRepository,
                            Path.Combine(largeRepository, "same-a"),
                            "same-a",
                            3,
                            200,
                            null,
                            new GitIgnoreMatch("same-a", null, null, null)),
                    ],
                    5,
                    400,
                    []),
            ],
            6,
            500,
            [new OperationWarning(Path.Combine(root, "warning"), "audit warning")]);
        return new AuditFixture(root, largeRepository, smallRepository, AuditReportDocument.FromAudit([root], result));
    }

    private sealed record AuditFixture(string Root, string LargeRepository, string SmallRepository, AuditReportDocument Report);
}
