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
    [Arguments("gs://bucket", "")]
    [Arguments("gs://bucket/", "")]
    [Arguments("gs://bucket/team/mida/", "team/mida/")]
    public async Task ParsesARemoteCacheBesideTheLocalOne(string remote, string prefix)
    {
        var options = BuildOptions.Parse(["repo.sln", "--cache", remote, "--cache", "store", "--read-only"]);

        Assert.NotNull(options);
        await Assert.That(options.Bucket).IsEqualTo("bucket");
        await Assert.That(options.BucketPrefix).IsEqualTo(prefix);
        await Assert.That(options.CacheDirectory).IsEqualTo(Path.GetFullPath("store"));
        await Assert.That(options.ReadOnly).IsTrue();
    }

    [Test]
    public async Task ParsesProperties()
    {
        var options = BuildOptions.Parse(["repo.sln", "-p:Configuration=Release", "-p:DefineConstants=A;B", "-p:Empty=", "--configuration", "Debug", "-p", "Spaced=yes"]);

        Assert.NotNull(options);
        await Assert.That(options.Properties).IsEquivalentTo(new Dictionary<string, string>
        {
            ["Configuration"] = "Debug",
            ["DefineConstants"] = "A;B",
            ["Empty"] = string.Empty,
            ["Spaced"] = "yes",
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

    [Test]
    [Arguments("gs://")]
    [Arguments("gs:///prefix")]
    public async Task RejectsABucketWithoutAName(string remote) =>
        await Assert.That(BuildOptions.Parse(["repo.sln", "--cache", remote])).IsNull();

    [Test]
    public async Task RejectsTwoConfigurations() =>
        await Assert.That(BuildOptions.Parse(["repo.sln", "-c", "Release", "-c", "Debug"])).IsNull();
}
