using Xunit;

namespace Mida.Tests;

public sealed class BuildOptionsTests
{
    [Fact]
    public void ParsesEveryOption()
    {
        var options = BuildOptions.Parse(["repo.sln", "--print-keys", "--key-details", "out"]);

        Assert.NotNull(options);
        Assert.Equal(Path.GetFullPath("repo.sln"), options.Solution);
        Assert.True(options.PrintKeys);
        Assert.Equal(Path.GetFullPath("out"), options.KeyDetailsDirectory);
    }

    [Theory]
    [InlineData("repo.sln", "--unknown")]
    [InlineData("repo.sln", "--key-details")]
    [InlineData("one.sln", "two.sln")]
    public void RejectsBadArguments(string first, string second) =>
        Assert.Null(BuildOptions.Parse([first, second]));
}
