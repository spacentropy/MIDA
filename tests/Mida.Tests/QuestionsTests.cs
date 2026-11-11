using System.Collections.Concurrent;

namespace Mida.Tests;

/// <summary>A stored graph is as good as these: each test changes one thing an evaluation could have seen.</summary>
public sealed class QuestionsTests : IDisposable
{
    // Ignoring case, as Checkout reads the environment.
    private static readonly Dictionary<string, string> Variables = new(StringComparer.OrdinalIgnoreCase) { ["SET"] = "1" };

    private readonly string temp = Directory.CreateTempSubdirectory("mida-tests-").FullName;
    private readonly string repo;
    private readonly PathTokens tokens;

    public QuestionsTests()
    {
        repo = Path.Combine(temp, "repo");
        Directory.CreateDirectory(Path.Combine(repo, "A"));
        File.WriteAllText(Path.Combine(repo, "A", "A.csproj"), "<Project />");
        tokens = Tokens(repo);
    }

    public void Dispose() => Directory.Delete(temp, recursive: true);

    private PathTokens Tokens(string root) => new(root, Path.Combine(temp, "packages"), Path.Combine(temp, "dotnet"), Path.Combine(temp, "profile"));

    private Questions Ask() => new(Path.Combine(temp, "dotnet"), new ConcurrentDictionary<string, string>(), Variables);

    private string? Changed(SnapshotQuestions stored, PathTokens? other = null, Dictionary<string, string>? variables = null) =>
        Questions.ChangedSource(stored, other ?? tokens, new ConcurrentDictionary<string, string>(), variables ?? Variables)
        ?? Questions.ChangedProbe(stored, other ?? tokens);

    private string In(params string[] parts) => Path.Combine([repo, .. parts]);

    [Test]
    public async Task NothingChangedWhenNothingDid()
    {
        var questions = Ask();
        questions.AddFile(In("A", "A.csproj"));
        questions.AddFile(In("Directory.Build.props"));
        questions.AddExists(Questions.Files, In("A", "A.csproj.user"), false);
        questions.AddExists(Questions.Directories, In("A"), true);
        questions.AddList(Questions.Files, In("A"), "*", recursive: false);
        questions.AddEnvironment("SET");
        questions.AddEnvironment("UNSET");

        var stored = questions.Store(tokens, placed: false);

        await Assert.That(questions.Unanswerable).IsNull();
        await Assert.That(stored.Root).IsNull();
        await Assert.That(Changed(stored)).IsNull();
    }

    [Test]
    public async Task EditedFileIsNamed()
    {
        var questions = Ask();
        questions.AddFile(In("A", "A.csproj"));
        var stored = questions.Store(tokens, placed: false);

        File.WriteAllText(In("A", "A.csproj"), "<Project><PropertyGroup /></Project>");

        await Assert.That(Changed(stored)).IsEqualTo("{RepoRoot}/A/A.csproj".Replace('/', Path.DirectorySeparatorChar));
    }

    [Test]
    public async Task FileThatWasNotThereIsNamedOnceItIs()
    {
        var questions = Ask();
        questions.AddFile(In("Directory.Build.props"));
        var stored = questions.Store(tokens, placed: false);

        File.WriteAllText(In("Directory.Build.props"), "<Project />");

        await Assert.That(Changed(stored)).IsNotNull();
    }

    [Test]
    [Arguments("New.cs", true)]
    // NuGet's and mida's own files beside the outputs: no stored graph brings them back, so a listing of obj/ counts without them.
    [Arguments("project.nuget.cache", false)]
    [Arguments("A.csproj.nuget.dgspec.json", false)]
    [Arguments("A.csproj.mida-restored.txt", false)]
    public async Task NewFileInAListedDirectoryChangesTheListing(string name, bool changes)
    {
        var questions = Ask();
        questions.AddList(Questions.Files, In("A"), "*", recursive: false);
        var stored = questions.Store(tokens, placed: false);

        File.WriteAllText(In("A", name), string.Empty);

        await Assert.That(Changed(stored) is not null).IsEqualTo(changes);
    }

    [Test]
    public async Task NewDirectoryChangesWhatExists()
    {
        var questions = Ask();
        questions.AddExists(Questions.Directories, In("A", "bin"), false);
        var stored = questions.Store(tokens, placed: false);

        Directory.CreateDirectory(In("A", "bin"));

        await Assert.That(Changed(stored)).IsNotNull();
    }

    [Test]
    [Arguments("SET", "2")]
    [Arguments("UNSET", "now set")]
    public async Task VariableThatWasReadIsNamedWhenItChanges(string name, string value)
    {
        var questions = Ask();
        questions.AddEnvironment("SET");
        questions.AddEnvironment("UNSET");
        var stored = questions.Store(tokens, placed: false);

        var changed = Changed(stored, variables: new Dictionary<string, string>(Variables) { [name] = value });

        await Assert.That(changed).IsEqualTo($"the environment variable {name}");
    }

    [Test]
    public async Task NameReadWhileUnsetIsNamedOnceAVariableHasIt()
    {
        var questions = Ask();
        questions.AddUnset("FEATURE");
        questions.AddUnset("set");
        var stored = questions.Store(tokens, placed: false);

        // A variable that is set says what it is through MSBuild's event, which AddEnvironment records.
        await Assert.That(stored.Environment.Keys).IsEquivalentTo(["FEATURE"]);
        await Assert.That(Changed(stored)).IsNull();
        await Assert.That(Changed(stored, variables: new Dictionary<string, string>(Variables) { ["FEATURE"] = "on" })).IsEqualTo("the environment variable FEATURE");
        await Assert.That(Changed(stored, variables: new Dictionary<string, string>(Variables, StringComparer.OrdinalIgnoreCase) { ["feature"] = "on" }))
            .IsEqualTo("the environment variable FEATURE");
    }

    // The SDK path in MSBuildExtensionsPath is a different directory on every machine, and means the same.
    [Test]
    public async Task VariableThatNamesARootFitsAnotherMachine()
    {
        var withPath = new Dictionary<string, string> { ["MSBuildExtensionsPath"] = Path.Combine(temp, "dotnet", "sdk", "10.0.100") };
        var questions = new Questions(Path.Combine(temp, "dotnet"), new ConcurrentDictionary<string, string>(), withPath);
        questions.AddEnvironment("MSBuildExtensionsPath");
        var stored = questions.Store(tokens, placed: false);

        var there = new PathTokens(Path.Combine(temp, "clone"), Path.Combine(temp, "packages"), Path.Combine(temp, "other dotnet"), Path.Combine(temp, "profile"));
        var thereVariables = new Dictionary<string, string> { ["MSBuildExtensionsPath"] = Path.Combine(temp, "other dotnet", "sdk", "10.0.100") };
        Directory.CreateDirectory(Path.Combine(temp, "clone"));

        await Assert.That(stored.Environment["MSBuildExtensionsPath"]).IsEqualTo(Path.Combine("{DotnetRoot}", "sdk", "10.0.100"));
        await Assert.That(Changed(stored, there, thereVariables)).IsNull();
        await Assert.That(Changed(stored, there, new Dictionary<string, string> { ["MSBuildExtensionsPath"] = Path.Combine(temp, "other dotnet", "sdk", "11.0.100") }))
            .IsEqualTo("the environment variable MSBuildExtensionsPath");
    }

    [Test]
    public async Task SameAnswersFitACheckoutAtAnotherPath()
    {
        var questions = Ask();
        questions.AddFile(In("A", "A.csproj"));
        questions.AddFile(Path.Combine(temp, "Directory.Build.props"));
        questions.AddList(Questions.Files, In("A"), "*", recursive: false);
        var stored = questions.Store(tokens, placed: false);

        var clone = Path.Combine(temp, "some", "deeper", "clone");
        Directory.CreateDirectory(Path.Combine(clone, "A"));
        File.Copy(In("A", "A.csproj"), Path.Combine(clone, "A", "A.csproj"));

        await Assert.That(Changed(stored, Tokens(clone))).IsNull();
    }

    // MSBuild looks for Directory.Build.props in every directory above a project. The clone has one where the stored
    // checkout had none, one directory above the repo.
    [Test]
    public async Task FileAboveACloneIsNamedWhereTheStoredCheckoutHadNone()
    {
        var questions = Ask();
        questions.AddFile(Path.Combine(temp, "Directory.Build.props"));
        var stored = questions.Store(tokens, placed: false);

        var clone = Path.Combine(temp, "some", "deeper", "clone");
        Directory.CreateDirectory(clone);
        File.WriteAllText(Path.Combine(temp, "some", "deeper", "Directory.Build.props"), "<Project />");

        await Assert.That(Changed(stored, Tokens(clone))).IsEqualTo("{Above:1}/Directory.Build.props");
    }

    [Test]
    public async Task PathOutsideEveryNameFitsOnlyTheSameCheckoutPath()
    {
        var elsewhere = Directory.CreateTempSubdirectory("mida-tests-elsewhere-").FullName;
        try
        {
            var questions = Ask();
            questions.AddFile(Path.Combine(elsewhere, "Shared.props"));
            var stored = questions.Store(tokens, placed: false);

            await Assert.That(stored.Root).IsEqualTo(repo);
            await Assert.That(Changed(stored)).IsNull();
            await Assert.That(Changed(stored, Tokens(Path.Combine(temp, "clone")))).IsEqualTo("the path of the checkout");
        }
        finally
        {
            Directory.Delete(elsewhere, recursive: true);
        }
    }

    [Test]
    public async Task GraphThatHoldsAPlacedPathFitsOnlyTheSameCheckoutPath() =>
        await Assert.That(Ask().Store(tokens, placed: true).Root).IsEqualTo(repo);

    // An answer MSBuild got that cannot be had again the same way would make a graph that never fits, or fits wrongly.
    [Test]
    public async Task AnswerThatDiffersFromMSBuildsIsUnanswerable()
    {
        var questions = Ask();

        questions.AddExists(Questions.Files, In("A", "A.csproj"), false);

        await Assert.That(questions.Unanswerable).IsNotNull();
    }

    [Test]
    public async Task SdkFilesAreNotAsked()
    {
        var questions = Ask();
        questions.AddFile(Path.Combine(temp, "dotnet", "sdk", "10.0.100", "Sdk.props"));
        questions.AddExists(Questions.Either, Path.Combine(temp, "dotnet", "packs"), false);

        var stored = questions.Store(tokens, placed: false);

        await Assert.That(stored.Files).IsEmpty();
        await Assert.That(stored.Exists).IsEmpty();
    }

    [Test]
    public async Task AnswersTellCheckoutsApart()
    {
        var questions = Ask();
        questions.AddFile(In("A", "A.csproj"));
        questions.AddList(Questions.Files, In("A"), "*", recursive: false);
        var stored = questions.Store(tokens, placed: false);
        string Answers() => Questions.Answers(stored, tokens, new ConcurrentDictionary<string, string>(), Variables);

        var before = Answers();
        await Assert.That(Answers()).IsEqualTo(before);

        File.WriteAllText(In("A", "New.cs"), string.Empty);

        await Assert.That(Answers()).IsNotEqualTo(before);
    }
}
