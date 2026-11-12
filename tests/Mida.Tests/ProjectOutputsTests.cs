using System.Text;
using System.Text.Json;

namespace Mida.Tests;

public sealed class ProjectOutputsTests
{
    private static readonly string Disk = Path.GetPathRoot(Path.GetTempPath())!;

    private static string At(params string[] parts) => Path.Combine([Disk, .. parts]);

    private static PathTokens Tokens(string repo) => new(At(repo), At("home", "me", ".nuget", "packages"), At("usr", "share", "dotnet"), At("home", "me"));

    // Serialized, so a Windows path has its backslashes escaped as the SDK writes them. A hash can start with a slash.
    private static byte[] Json(string repo, string packages) =>
        JsonSerializer.SerializeToUtf8Bytes(new[] { At(repo, "Rcl", "wwwroot") + Path.DirectorySeparatorChar, At(packages, "x", "staticwebassets"), "/", "/H9DYsP2Q1bCz/KRdAKuIP8Qt3rmog0=" });

    [Test]
    public async Task StaticWebAssetManifestComesBackWithAnotherMachinesPaths()
    {
        var stored = ProjectOutputs.Relocation(Tokens("a")).Store("Web/bin/Web.staticwebassets.runtime.json", Json("a", Path.Combine("home", "me", ".nuget", "packages")));

        Assert.NotNull(stored);
        await Assert.That(Encoding.UTF8.GetString(stored)).DoesNotContain(At("a"));
        var there = new PathTokens(At("b", "deeper"), At("packages"), At("opt", "dotnet"), At("root"));
        await Assert.That(ProjectOutputs.Relocation(there).Restore(stored)).IsEquivalentTo(Json(Path.Combine("b", "deeper"), "packages"));
    }

    // Its writer escapes each slash, which a Windows path has none of.
    [Test]
    public async Task TestHostManifestComesBackWithAnotherCheckoutsPath()
    {
        static byte[] Manifest(string root) => Encoding.UTF8.GetBytes($$"""{"Web": {{JsonSerializer.Serialize(Path.Combine(root, "Web")).Replace("/", "\\/", StringComparison.Ordinal)}}}""");

        var stored = ProjectOutputs.Relocation(Tokens("a")).Store("Tests/bin/MvcTestingAppManifest.json", Manifest(At("a")));

        Assert.NotNull(stored);
        var there = new PathTokens(At("b", "deeper"), At("packages"), At("opt", "dotnet"), At("root"));
        await Assert.That(ProjectOutputs.Relocation(there).Restore(stored)).IsEquivalentTo(Manifest(At("b", "deeper")));
    }

    [Test]
    [Arguments("Web/bin/Web.dll")]
    [Arguments("Web/obj/rpswa.dswa.cache.json")]
    public async Task OtherFilesAreLeftAlone(string path) =>
        await Assert.That(ProjectOutputs.Relocation(Tokens("a")).Store(path, Encoding.UTF8.GetBytes(At("a", "x")))).IsNull();

    [Test]
    [Arguments("Web/obj/staticwebassets.build.json")]
    [Arguments("Web/bin/Web.staticwebassets.endpoints.json")]
    public async Task StaticWebAssetManifestThatNamesAPathNoTokenCoversIsNotStored(string path) =>
        await Assert.That(() => ProjectOutputs.Relocation(Tokens("a")).Store(path, JsonSerializer.SerializeToUtf8Bytes(new[] { Path.GetTempPath() })))
            .ThrowsExactly<InvalidDataException>();
}
