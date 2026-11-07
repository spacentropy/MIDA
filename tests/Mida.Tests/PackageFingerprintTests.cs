using System.Text;

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

    [Test]
    public async Task AbsolutePathsDoNotMatter() =>
        await Assert.That(Fingerprint(Assets("/Users/me/.nuget/", "/src/b/App.csproj", "1.0.0")))
            .IsEqualTo(Fingerprint(Assets("/home/ci/.nuget/", "/work/a/App.csproj", "1.0.0")));

    [Test]
    public async Task ResolvedVersionMatters() =>
        await Assert.That(Fingerprint(Assets("/x/", "/y/App.csproj", "1.0.1")))
            .IsNotEqualTo(Fingerprint(Assets("/x/", "/y/App.csproj", "1.0.0")));
}
