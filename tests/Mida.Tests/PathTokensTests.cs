using System.Text;

namespace Mida.Tests;

public sealed class PathTokensTests
{
    private static readonly string Disk = Path.GetPathRoot(Path.GetTempPath())!;

    private static string At(params string[] parts) => Path.Combine([Disk, .. parts]);

    private static string Slashes(string token) => token.Replace('\\', '/');

    // A CI runner's layout: the repo and the package folder are both under the profile.
    private static PathTokens Tokens(params string[] repo) =>
        new(At(repo.Length == 0 ? ["home", "me", "work", "repo"] : repo), At("home", "me", ".nuget", "packages"), At("usr", "share", "dotnet"), At("home", "me"));

    [Test]
    public async Task RootIsReplacedOnlyWhereAPathStartsWithIt()
    {
        var repo = At("home", "me", "work", "repo");
        var text = $"""<a p="{repo}"/><b>{repo}{Path.DirectorySeparatorChar}src;{repo}2{Path.DirectorySeparatorChar}src</b>x{repo}""";

        var tokenized = Tokens().Tokenize(text);

        await Assert.That(tokenized).IsEqualTo($"""<a p="{"{RepoRoot}"}"/><b>{"{RepoRoot}"}{Path.DirectorySeparatorChar}src;{"{UserProfile}"}{Path.DirectorySeparatorChar}work{Path.DirectorySeparatorChar}repo2{Path.DirectorySeparatorChar}src</b>x{repo}""");
    }

    [Test]
    public async Task LongestRootWins() =>
        await Assert.That(Slashes(Tokens().Tokenize(At("home", "me", ".nuget", "packages", "tunit", "1.0.0")))).IsEqualTo("{NuGetPackageRoot}/tunit/1.0.0");

    [Test]
    public async Task TextComesBackWithAnotherMachinesPaths()
    {
        var here = Tokens();
        var there = new PathTokens(At("src"), At("packages"), At("opt", "dotnet"), At("root"));
        var text = $"{At("home", "me", "work", "repo", "A", "obj")}|{At("home", "me", ".nuget", "packages", "x")}|{At("usr", "share", "dotnet", "sdk")}|{At("home", "me", ".config")}";

        var moved = there.Detokenize(here.Tokenize(text));

        await Assert.That(moved).IsEqualTo($"{At("src", "A", "obj")}|{At("packages", "x")}|{At("opt", "dotnet", "sdk")}|{At("root", ".config")}");
        await Assert.That(here.Detokenize(here.Tokenize(text))).IsEqualTo(text);
    }

    [Test]
    public async Task BytesKeepTheirByteOrderMark()
    {
        byte[] marked = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(At("home", "me", "work", "repo", "A"))];

        var tokenized = Tokens().Tokenize(marked);

        Assert.NotNull(tokenized);
        await Assert.That(tokenized.AsSpan().StartsWith<byte>([0xEF, 0xBB, 0xBF])).IsTrue();
        await Assert.That(Tokens().Detokenize(tokenized)).IsEquivalentTo(marked);
    }

    [Test]
    public async Task BytesThatAreNoTextHaveNoOtherForm() =>
        await Assert.That(Tokens().Tokenize([0xFF, 0xFE, 0x00, 0xC3])).IsNull();

    [Test]
    public async Task FileAboveTheRepoIsNamedByHowFarAbove()
    {
        var tokens = Tokens();

        await Assert.That(tokens.TokenizePath(At("home", "me", "work", "Directory.Build.props"))).IsEqualTo("{Above:1}/Directory.Build.props");
        await Assert.That(tokens.TokenizePath(At("global.json"))).IsEqualTo("{Above:4}/global.json");
        await Assert.That(Slashes(tokens.TokenizePath(At("home", "me", "work", "repo", "global.json")))).IsEqualTo("{RepoRoot}/global.json");
    }

    // The profile is one of the directories above this repo, and a search upwards from a project passes through it.
    [Test]
    public async Task DirectoryAboveTheRepoComesBeforeTheProfile() =>
        await Assert.That(Tokens().TokenizePath(At("home", "me", "Directory.Build.props"))).IsEqualTo("{Above:2}/Directory.Build.props");

    [Test]
    public async Task FileAboveComesBackAsFarAboveAnotherCheckout()
    {
        var deeper = Tokens("home", "me", "work", "some", "deeper", "repo");

        await Assert.That(deeper.DetokenizePath("{Above:1}/global.json")).IsEqualTo(At("home", "me", "work", "some", "deeper", "global.json"));
        await Assert.That(deeper.DetokenizePath("{Above:6}/global.json")).IsEqualTo(At("global.json"));
        // Further above than this checkout has directories: nothing can be there.
        await Assert.That(deeper.DetokenizePath("{Above:7}/global.json")).IsEqualTo(At("{Above:7}", "global.json"));
    }

    // On another machine the directories above the repo and above the package folder are different ones.
    [Test]
    public async Task QuestionIsAskedUnderEveryNameThatFits()
    {
        var names = Tokens().TokenizeQuestion(At(".editorconfig")).ToList();

        await Assert.That(names).IsEquivalentTo(["{Above:4}/.editorconfig", "{AboveUserProfile:2}/.editorconfig", "{AboveNuGetPackageRoot:4}/.editorconfig"]);
        await Assert.That(Tokens().TokenizeQuestion(At("home", "me", ".nuget", ".editorconfig")).Select(Slashes))
            .IsEquivalentTo(["{UserProfile}/.nuget/.editorconfig", "{AboveNuGetPackageRoot:1}/.editorconfig"]);
    }

    // Named as one directory above itself, a repo at /work/a would be asked about /other/a in a clone at /other/b.
    [Test]
    public async Task RootIsNamedOnlyByItsToken()
    {
        var tokens = Tokens();

        await Assert.That(tokens.TokenizeQuestion(At("home", "me", "work", "repo")).ToList()).IsEquivalentTo(["{RepoRoot}"]);
        await Assert.That(tokens.TokenizeQuestion(At("home", "me")).ToList()).IsEquivalentTo(["{UserProfile}"]);
        await Assert.That(tokens.TokenizePath(At("home", "me", ".nuget", "packages"))).IsEqualTo("{NuGetPackageRoot}");
    }

    [Test]
    [Arguments("repo & co", false)]
    [Arguments("repo'1", false)]
    [Arguments("100%", false)]
    [Arguments("a;b", false)]
    [Arguments("some deeper (b)", true)]
    [Arguments("répo-1.2_x", true)]
    public async Task RootThatTextWouldEscapeIsNotPlain(string name, bool plain) =>
        await Assert.That(Tokens("work", name).Plain).IsEqualTo(plain);

    [Test]
    public async Task PathNoNameFitsStaysAndIsPlaced()
    {
        var elsewhere = At("opt", "shared", "Common.props");

        await Assert.That(Tokens().TokenizePath(elsewhere)).IsEqualTo(elsewhere);
        await Assert.That(PathTokens.IsPlaced(elsewhere)).IsTrue();
        await Assert.That(PathTokens.IsPlaced("{RepoRoot}/a")).IsFalse();
        await Assert.That(PathTokens.IsPlaced("{Above:1}/a")).IsFalse();
    }

    // A graph stored two directories below the root asked two levels up. A checkout four below has two more to ask.
    [Test]
    public async Task SearchUpwardsGoesOnAboveWhereTheStoredCheckoutEnded()
    {
        var shallow = Tokens("work", "repo");
        var deep = Tokens("home", "me", "work", "repo");

        var higher = deep.Higher("{Above:1}/global.json", shallow.Depths).ToList();

        await Assert.That(shallow.Depths[0]).IsEqualTo(2);
        await Assert.That(higher).IsEquivalentTo([At("home", "global.json"), At("global.json")]);
        await Assert.That(shallow.Higher("{Above:1}/global.json", deep.Depths)).IsEmpty();
        await Assert.That(deep.Higher("{RepoRoot}/global.json", shallow.Depths)).IsEmpty();
    }
}
