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

    // A runner argument or a -p value may hold a newline.
    [Test]
    public async Task ALineWithANewlineIsNotTwoLines() =>
        await Assert.That(CacheKey.From(["argument 0 a\nargument 1 b"]).Value).IsNotEqualTo(CacheKey.From(["argument 0 a", "argument 1 b"]).Value);
}
