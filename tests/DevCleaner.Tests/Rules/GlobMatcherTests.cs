using System.Text.RegularExpressions;
using DevCleaner.Rules;

namespace DevCleaner.Tests.Rules;

public sealed class GlobMatcherTests
{
    [Fact]
    public void GetOrCreateRegex_caches_normalized_dynamic_patterns()
    {
        var first = GlobMatcher.GetOrCreateRegex("**\\obj\\**");
        var equivalent = GlobMatcher.GetOrCreateRegex("**/obj/**");

        Assert.Same(first, equivalent);
        Assert.False(first.Options.HasFlag(RegexOptions.Compiled));
    }

    [Theory]
    [InlineData("bin/*", "bin/debug", true)]
    [InlineData("bin/*", "src/bin/debug", false)]
    [InlineData("**/obj/**", "obj/project.assets.json", true)]
    [InlineData("**/obj/**", "src/app/obj/project.assets.json", true)]
    [InlineData("**/obj/**", "obj", true)]
    [InlineData("**/obj/**", "src/object/file", false)]
    [InlineData("artifacts/?.zip", "artifacts/a.zip", true)]
    [InlineData("artifacts/?.zip", "artifacts/ab.zip", false)]
    [InlineData("**/node_modules/**", "packages\\web\\node_modules\\react\\index.js", true)]
    [InlineData("**/.next/**", ".next/cache/data", true)]
    [InlineData("**/.nuxt/**", ".nuxt/build.json", true)]
    [InlineData("**/.svelte-kit/**", ".svelte-kit/output.js", true)]
    [InlineData("**/.gradle/**", ".gradle/cache.bin", true)]
    [InlineData("**/.pytest_cache/**", ".pytest_cache/v/cache", true)]
    [InlineData("**/.venv/**", ".venv/bin/python", true)]
    public void IsMatch_applies_anchored_normalized_glob_semantics(string pattern, string path, bool expected)
    {
        Assert.Equal(expected, GlobMatcher.IsMatch(pattern, path));
    }
}
