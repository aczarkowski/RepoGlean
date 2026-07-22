using DevCleaner.Rules;

namespace DevCleaner.Tests.Rules;

public sealed class GlobMatcherTests
{
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
    public void IsMatch_applies_anchored_normalized_glob_semantics(string pattern, string path, bool expected)
    {
        Assert.Equal(expected, GlobMatcher.IsMatch(pattern, path));
    }
}
