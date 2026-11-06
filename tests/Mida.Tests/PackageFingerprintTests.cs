using System.Text;
using Xunit;

namespace Mida.Tests;

public sealed class PackageFingerprintTests
{
    private static string Assets(string packageFolder, string projectPath, string version) =>
        $$"""
        {
          "version": 3,
          "targets": { "net10.0": { "Foo/{{version}}": { "type": "package" } } },
          "libraries": { "Foo/{{version}}": { "sha512": "abc", "type": "package" } },
          "projectFileDependencyGroups": { "net10.0": [ "Foo >= {{version}}" ] },
          "packageFolders": { "{{packageFolder}}": {} },
          "project": { "restore": { "projectPath": "{{projectPath}}" } }
        }
        """;

    private static string Fingerprint(string assets) => PackageFingerprint.Compute(Encoding.UTF8.GetBytes(assets));

    [Fact]
    public void AbsolutePathsDoNotMatter() => Assert.Equal(
        Fingerprint(Assets("/home/ci/.nuget/", "/work/a/App.csproj", "1.0.0")),
        Fingerprint(Assets("/Users/me/.nuget/", "/src/b/App.csproj", "1.0.0")));

    [Fact]
    public void ResolvedVersionMatters() => Assert.NotEqual(
        Fingerprint(Assets("/x/", "/y/App.csproj", "1.0.0")),
        Fingerprint(Assets("/x/", "/y/App.csproj", "1.0.1")));
}
