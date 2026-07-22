using DevCleaner.Cli;

namespace DevCleaner.Rules;

public static class BuiltInRules
{
    public static IReadOnlyList<ArtifactRule> All { get; } =
    [
        Rule("dotnet.bin", ArtifactCategory.Build, ["**/bin", "**/bin/**"], ["**/*.sln", "**/*.csproj", "**/*.fsproj", "**/*.vbproj"]),
        Rule("dotnet.obj", ArtifactCategory.Build, ["**/obj", "**/obj/**"], ["**/*.sln", "**/*.csproj", "**/*.fsproj", "**/*.vbproj"]),
        Rule("dotnet.test-results", ArtifactCategory.Test, ["**/TestResults", "**/TestResults/**"], ["**/*.sln", "**/*.csproj", "**/*.fsproj", "**/*.vbproj"]),
        Rule("node.node-modules", ArtifactCategory.Dependency, ["**/node_modules", "**/node_modules/**"], ["**/package.json"], false),
        Rule("node.next", ArtifactCategory.Build, ["**/.next", "**/.next/**"], ["**/package.json"]),
        Rule("node.nuxt", ArtifactCategory.Build, ["**/.nuxt", "**/.nuxt/**"], ["**/package.json"]),
        Rule("node.svelte-kit", ArtifactCategory.Build, ["**/.svelte-kit", "**/.svelte-kit/**"], ["**/package.json"]),
        Rule("jvm.maven-target", ArtifactCategory.Build, ["**/target", "**/target/**"], ["**/pom.xml"]),
        Rule("jvm.gradle-build", ArtifactCategory.Build, ["**/build", "**/build/**"], ["**/build.gradle", "**/build.gradle.kts", "**/settings.gradle", "**/settings.gradle.kts"]),
        Rule("jvm.gradle-cache", ArtifactCategory.Cache, ["**/.gradle", "**/.gradle/**"], ["**/build.gradle", "**/build.gradle.kts", "**/settings.gradle", "**/settings.gradle.kts"]),
        Rule("rust.target", ArtifactCategory.Build, ["**/target", "**/target/**"], ["**/Cargo.toml"]),
        Rule("python.pycache", ArtifactCategory.Cache, ["**/__pycache__", "**/__pycache__/**"], ["**/pyproject.toml", "**/requirements.txt", "**/setup.py"]),
        Rule("python.pytest-cache", ArtifactCategory.Test, ["**/.pytest_cache", "**/.pytest_cache/**"], ["**/pyproject.toml", "**/requirements.txt", "**/setup.py"]),
        Rule("python.venv", ArtifactCategory.Dependency, ["**/.venv", "**/.venv/**", "**/venv", "**/venv/**"], ["**/pyproject.toml", "**/requirements.txt", "**/setup.py"], false),
        Rule("go.bin", ArtifactCategory.Build, ["**/bin", "**/bin/**"], ["**/go.mod"]),
        Rule("go.coverage", ArtifactCategory.Test, ["**/coverage.out"], ["**/go.mod"]),
        Rule("cpp.cmake-build", ArtifactCategory.Build, ["**/cmake-build-*", "**/cmake-build-*/**"], ["**/CMakeLists.txt"]),
        Rule("cpp.build", ArtifactCategory.Build, ["**/build", "**/build/**", "**/out", "**/out/**"], ["**/CMakeLists.txt", "**/Makefile"]),
        Rule("apple.derived-data", ArtifactCategory.Cache, ["**/DerivedData", "**/DerivedData/**"], ["**/*.xcodeproj/project.pbxproj", "**/Package.swift"]),
        Rule("apple.build", ArtifactCategory.Build, ["**/build", "**/build/**"], ["**/*.xcodeproj/project.pbxproj", "**/Package.swift"]),
    ];

    public static IReadOnlyList<string> Ids { get; } = All.Select(rule => rule.Id).ToArray();

    private static ArtifactRule Rule(string id, ArtifactCategory category, IReadOnlyList<string> patterns, IReadOnlyList<string> markers, bool preselected = true) =>
        new(id, category, patterns, markers, preselected);
}
