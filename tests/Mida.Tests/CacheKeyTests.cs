using Xunit;

namespace Mida.Tests;

public sealed class CacheKeyTests
{
    [Fact]
    public void OrderAndDuplicatesDoNotMatter() =>
        Assert.Equal(CacheKey.From(["a", "b"]).Value, CacheKey.From(["b", "a", "b"]).Value);

    [Fact]
    public void ContentMatters() =>
        Assert.NotEqual(CacheKey.From(["a", "b"]).Value, CacheKey.From(["a", "c"]).Value);

    [Fact]
    public void LinesCannotRunTogether() =>
        Assert.NotEqual(CacheKey.From(["ab", "c"]).Value, CacheKey.From(["a", "bc"]).Value);
}
