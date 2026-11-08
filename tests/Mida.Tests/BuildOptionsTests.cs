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
    public async Task ParsesProperties()
    {
        var options = BuildOptions.Parse(["repo.sln", "-c", "Release", "-p:DefineConstants=A;B", "-p:Empty=", "--configuration", "Debug"]);

        Assert.NotNull(options);
        await Assert.That(options.Properties).IsEquivalentTo(new Dictionary<string, string>
        {
            ["Configuration"] = "Debug",
            ["DefineConstants"] = "A;B",
            ["Empty"] = string.Empty,
        });
    }

    [Test]
    [Arguments("repo.sln", "--unknown")]
    [Arguments("repo.sln", "--key-details")]
    [Arguments("repo.sln", "--cache")]
    [Arguments("repo.sln", "-c")]
    [Arguments("repo.sln", "-p:NoValue")]
    [Arguments("repo.sln", "-p:=value")]
    [Arguments("one.sln", "two.sln")]
    public async Task RejectsBadArguments(string first, string second) =>
        await Assert.That(BuildOptions.Parse([first, second])).IsNull();
}
