namespace Mida.Tests;

public sealed class CacheKeyTests
{
    [Test]
    public async Task OrderAndDuplicatesDoNotMatter() =>
        await Assert.That(CacheKey.From(["b", "a", "b"]).Value).IsEqualTo(CacheKey.From(["a", "b"]).Value);

    [Test]
    public async Task ContentMatters() =>
        await Assert.That(CacheKey.From(["a", "c"]).Value).IsNotEqualTo(CacheKey.From(["a", "b"]).Value);

    [Test]
    public async Task LinesCannotRunTogether() =>
        await Assert.That(CacheKey.From(["a", "bc"]).Value).IsNotEqualTo(CacheKey.From(["ab", "c"]).Value);
}
