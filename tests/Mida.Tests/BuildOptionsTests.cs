namespace Mida.Tests;

public sealed class BuildOptionsTests
{
    [Test]
    public async Task ParsesEveryOption()
    {
        var options = BuildOptions.Parse(["repo.sln", "--print-keys", "--key-details", "out", "--cache", "store"]);

        Assert.NotNull(options);
        await Assert.That(options.Solution).IsEqualTo(Path.GetFullPath("repo.sln"));
        await Assert.That(options.PrintKeys).IsTrue();
        await Assert.That(options.KeyDetailsDirectory).IsEqualTo(Path.GetFullPath("out"));
        await Assert.That(options.CacheDirectory).IsEqualTo(Path.GetFullPath("store"));
    }

    [Test]
    [Arguments("repo.sln", "--unknown")]
    [Arguments("repo.sln", "--key-details")]
    [Arguments("repo.sln", "--cache")]
    [Arguments("one.sln", "two.sln")]
    public async Task RejectsBadArguments(string first, string second) =>
        await Assert.That(BuildOptions.Parse([first, second])).IsNull();
}
