using static Mida.Tests.TestRepo;

namespace Mida.Tests;

/// <summary>
/// Real builds of App → LibA → LibB and Gen that ask where the graph came from. From the store, a build needs neither a
/// restore nor MSBuild's evaluation, so every test here is about when that is safe. See docs/notes/stored-graph.md
/// </summary>
public sealed class StoredGraphTests : IDisposable
{
    private const string Stored = "Graph from the store, restore skipped";
    private const string StoredAfterRestore = "Graph from the store\n";
    private const string NoneStored = "Graph evaluated: none is stored";
    private const string AllHits = "4 hits, 0 misses";

    // With it mida says whether it loaded MSBuild.
    private static readonly Dictionary<string, string> Timings = new() { ["MIDA_TIMINGS"] = "1" };

    private readonly string temp = Temp();

    public void Dispose() => Directory.Delete(temp, recursive: true);

    private string Checkout(string relativePath) => TestRepo.Checkout(Path.Combine(temp, relativePath));

    private string Build(string root, params string[] arguments) => Build(root, null, Timings, arguments);

    private string Build(string root, string? packages, Dictionary<string, string> environment, params string[] arguments)
    {
        var (exitCode, output) = Start(
            "dotnet", root, packages, environment, [Path.Combine(AppContext.BaseDirectory, "Mida.dll"), "build", "--cache", Path.Combine(temp, "cache"), .. arguments]);
        if (exitCode != 0)
        {
            Assert.Fail(output);
        }

        return output.ReplaceLineEndings("\n");
    }

    private static Dictionary<string, string> Keys(string output) => output.Split('\n')
        .Select(line => line.Split(' '))
        .Where(parts => parts is [{ Length: 64 }, _])
        .ToDictionary(parts => parts[1], parts => parts[0]);

    private static void Edit(string root, string file, string find, string replace)
    {
        var path = Path.Combine(root, file);
        var content = File.ReadAllText(path);
        if (!content.Contains(find, StringComparison.Ordinal))
        {
            Assert.Fail($"{find} is not in {file}");
        }

        File.WriteAllText(path, content.Replace(find, replace, StringComparison.Ordinal));
    }

    [Test]
    public async Task CleanCloneAtAnotherPathNeedsNeitherRestoreNorMSBuild()
    {
        var first = Build(Checkout("a"));
        await Assert.That(first).Contains(NoneStored);
        await Assert.That(first).Contains("MSBuild was loaded");

        var clone = Checkout("some deeper/path (b)");
        var output = Build(clone);

        await Assert.That(output).Contains(Stored);
        await Assert.That(output).Contains(AllHits);
        await Assert.That(output).Contains("MSBuild was not loaded");

        // The restore outputs came from the store, and MSBuild takes them: LibB needs its package to compile.
        await Assert.That(File.Exists(Path.Combine(clone, "LibB", "obj", "project.assets.json"))).IsTrue();
        Dotnet(clone, null, "build", "LibB/LibB.csproj", "--no-restore");
    }

    [Test]
    public async Task KeysFromTheStoredGraphAreTheKeysOfAnEvaluation()
    {
        Build(Checkout("a"));

        var stored = Build(Checkout("b"), "--print-keys");
        var evaluated = Build(Checkout("c/deeper"), "--print-keys", "--evaluate");

        await Assert.That(stored).Contains(Stored);
        await Assert.That(evaluated).Contains("Graph evaluated: --evaluate");
        await Assert.That(Keys(stored).Count).IsEqualTo(4);
        await Assert.That(Keys(stored)).IsEquivalentTo(Keys(evaluated));
    }

    [Test]
    [Arguments("Directory.Build.props", "</PropertyGroup>", "<NoWarn>CS0000</NoWarn></PropertyGroup>", "Directory.Build.props", "0 hits, 4 misses")]
    [Arguments("Directory.Packages.props", "13.0.3", "13.0.2", "Directory.Packages.props", "0 hits, 4 misses")]
    [Arguments("LibB/LibB.csproj", "</PropertyGroup>", "<NoWarn>CS0000</NoWarn></PropertyGroup>", "LibB/LibB.csproj", " hits, ")]
    [Arguments("global.json", "\"test\"", "\"msbuild-sdks\": { }, \"test\"", "global.json", " hits, ")]
    // A file that is new: null for what to find, and the content it gets.
    [Arguments("LibB/New.cs", null, "namespace LibB; public static class New { }", "LibB", "1 hits, 3 misses")]
    [Arguments("LibB/LibB.csproj.user", null, "<Project />", "LibB/LibB.csproj.user", " hits, ")]
    [Arguments(".editorconfig", null, "root = true\n", ".editorconfig", "0 hits, 4 misses")]
    [Arguments("Directory.Build.targets", null, "<Project />", "Directory.Build.targets", "0 hits, 4 misses")]
    public async Task ChangeAnEvaluationWouldSeeEvaluatesAgain(string file, string? find, string replace, string changed, string expected)
    {
        ArgumentNullException.ThrowIfNull(changed);
        Build(Checkout("a"));
        var clone = Checkout("b");
        if (find is null)
        {
            Write(clone, file, replace);
        }
        else
        {
            Edit(clone, file, find, replace);
        }

        var output = Build(clone);

        await Assert.That(output).Contains($"Graph evaluated: {changed.Replace('/', Path.DirectorySeparatorChar)} changed");
        await Assert.That(output).Contains(expected);
    }

    // An edit no evaluation reads: the graph still fits, MSBuild builds the one miss, and what it stores hits later.
    [Test]
    public async Task SourceEditKeepsTheStoredGraphAndBuildsTheMiss()
    {
        Build(Checkout("a"));

        var clone = Checkout("b");
        Edit(clone, "LibB/B.cs", """=> "b";""", """=> "edited";""");
        var output = Build(clone);

        await Assert.That(output).Contains(Stored);
        await Assert.That(output).Contains("3 hits, 1 misses");
        await Assert.That(output).Contains("MSBuild was loaded");

        var second = Checkout("c");
        Edit(second, "LibB/B.cs", """=> "b";""", """=> "edited";""");
        output = Build(second);

        await Assert.That(output).Contains(Stored);
        await Assert.That(output).Contains(AllHits);
    }

    [Test]
    public async Task VariableAProjectReadsEvaluatesAgainWhenItChanges()
    {
        string WithSwitch(string root)
        {
            Edit(root, "Directory.Build.props", "</Project>", """
                <PropertyGroup Condition="'$(MIDA_TEST_SWITCH)' == 'on'"><DefineConstants>$(DefineConstants);SWITCHED</DefineConstants></PropertyGroup>
                </Project>
                """);
            return root;
        }

        Dictionary<string, string> Switch(string value) => new(Timings) { ["MIDA_TEST_SWITCH"] = value };
        Build(WithSwitch(Checkout("a")), null, Switch("off"));

        await Assert.That(Build(WithSwitch(Checkout("b")), null, Switch("off"))).Contains(Stored);
        await Assert.That(Build(WithSwitch(Checkout("c")), null, Switch("on"))).Contains("Graph evaluated: the environment variable MIDA_TEST_SWITCH changed");
    }

    // MSBuild reports reading a variable only when it is set. An item that only an unset one leaves out must still show up.
    [Test]
    public async Task VariableThatWasUnsetEvaluatesAgainOnceItIsSet()
    {
        string WithFeature(string root)
        {
            Edit(root, "Directory.Build.props", "</Project>", """
                <ItemGroup Condition="'$(MIDA_TEST_FEATURE)' == 'on'"><Compile Include="$(MSBuildThisFileDirectory)Feature.cs" /></ItemGroup>
                </Project>
                """);
            Write(root, "Feature.cs", "public static class Feature { }");
            return root;
        }

        Build(WithFeature(Checkout("a")));
        await Assert.That(Build(WithFeature(Checkout("b")))).Contains(Stored);

        var output = Build(WithFeature(Checkout("c")), null, new Dictionary<string, string>(Timings) { ["MIDA_TEST_FEATURE"] = "on" });

        await Assert.That(output).Contains("Graph evaluated: the environment variable MIDA_TEST_FEATURE changed");
        await Assert.That(output).Contains("0 hits, 4 misses");
    }

    // A restore runs targets, and what their bodies read is in no question. Only the SDK's are fixed by its version.
    [Test]
    public async Task RestoreThatRunsARepoTargetIsNeverSkipped()
    {
        string WithTarget(string root, string packages)
        {
            Write(root, "Directory.Build.targets", """
                <Project>
                  <Target Name="AddFromFile" BeforeTargets="CollectPackageReferences">
                    <ReadLinesFromFile File="$(MSBuildThisFileDirectory)packages.txt"><Output TaskParameter="Lines" ItemName="_Extra" /></ReadLinesFromFile>
                    <ItemGroup><PackageReference Include="@(_Extra)" /></ItemGroup>
                  </Target>
                </Project>
                """);
            Write(root, "packages.txt", packages);
            return root;
        }

        Build(WithTarget(Checkout("a"), string.Empty));

        var same = Build(WithTarget(Checkout("b"), string.Empty));
        await Assert.That(same).Contains(StoredAfterRestore);
        await Assert.That(same).Contains(AllHits);

        // Gen references no package, so only the target puts one in its assets, and Gen and what uses it miss.
        var clone = WithTarget(Checkout("c"), "Newtonsoft.Json");
        var output = Build(clone);
        await Assert.That(output).Contains(StoredAfterRestore);
        await Assert.That(output).DoesNotContain(AllHits);
        await Assert.That(File.ReadAllText(Path.Combine(clone, "Gen", "obj", "project.assets.json"))).Contains("Newtonsoft.Json/13.0.3");
    }

    // Restore evaluates with ExcludeRestorePackageImports, the build without, so a file that reads it can differ between them.
    [Test]
    public async Task RestoreOfAFileThatReadsARestoreOnlyPropertyIsNeverSkipped()
    {
        string WithCondition(string root)
        {
            Edit(root, "Directory.Build.props", "</Project>", """
                <PropertyGroup Condition="'$(ExcludeRestorePackageImports)' == 'true'"><Restoring>true</Restoring></PropertyGroup>
                </Project>
                """);
            return root;
        }

        Build(WithCondition(Checkout("a")));

        var output = Build(WithCondition(Checkout("b")));

        await Assert.That(output).Contains(StoredAfterRestore);
        await Assert.That(output).Contains(AllHits);
    }

    // A floating version can resolve higher with no file changed, so its restore is never skipped. The graph still fits.
    [Test]
    public async Task FloatingVersionIsRestoredEveryTime()
    {
        var feed = Path.Combine(temp, "feed");
        var source = Path.Combine(temp, "source");
        var packages = Path.Combine(temp, "packages");
        Directory.CreateDirectory(feed);
        Write(source, "Mida.Test.Floating.csproj", Project(string.Empty));
        Write(source, "C.cs", "namespace Floating; public static class C { }");
        Dotnet(source, packages, "pack", "-p:Version=1.0.0", "-o", feed);

        string Repo(string name)
        {
            var root = Path.Combine(temp, name);
            Write(root, "global.json", GlobalJson);
            Write(root, "nuget.config", $"""<configuration><packageSources><clear /><add key="feed" value="{feed}" /></packageSources></configuration>""");
            Write(root, "Repo.slnx", """<Solution><Project Path="Lib/Lib.csproj" /></Solution>""");
            Write(root, "Lib/Lib.csproj", Project("""<PackageReference Include="Mida.Test.Floating" Version="1.*" />"""));
            Write(root, "Lib/C.cs", "public static class C { }");
            return root;
        }

        Build(Repo("a"), packages, Timings);

        var same = Build(Repo("b"), packages, Timings);
        await Assert.That(same).Contains(StoredAfterRestore);
        await Assert.That(same).Contains("1 hits, 0 misses");

        Dotnet(source, packages, "pack", "-p:Version=1.1.0", "-o", feed);

        var higher = Build(Repo("c"), packages, Timings);
        await Assert.That(higher).Contains(StoredAfterRestore);
        await Assert.That(higher).Contains("0 hits, 1 misses");
    }

    // Another machine's package folder is empty at first. A restore fills it, and the graph is not evaluated again.
    // Both folders come from NUGET_PACKAGES: NuGet writes the default one with a slash at its end and these without,
    // and props that differ even by that are another evaluation.
    [Test]
    public async Task EmptyPackageFolderIsRestoredAndTheGraphStillFits()
    {
        Build(Checkout("a"), Path.Combine(temp, "packages of a"), Timings);
        var packages = Path.Combine(temp, "packages");

        var first = Build(Checkout("b"), packages, Timings);
        await Assert.That(first).Contains(StoredAfterRestore);
        await Assert.That(first).Contains(AllHits);
        await Assert.That(Directory.Exists(Path.Combine(packages, "newtonsoft.json"))).IsTrue();

        var second = Build(Checkout("c"), packages, Timings);
        await Assert.That(second).Contains(Stored);
        await Assert.That(second).Contains(AllHits);
    }

    // A NuGet config can hold credentials, and a CI job writes a new token into one every run. Mida never reads one.
    [Test]
    public async Task NuGetConfigIsNeverRead()
    {
        Build(Checkout("a"));
        var clone = Checkout("b");
        Write(clone, "nuget.config", """
            <configuration><packageSourceCredentials><feed><add key="ClearTextPassword" value="secret" /></feed></packageSourceCredentials></configuration>
            """);

        var output = Build(clone);

        await Assert.That(output).Contains(Stored);
        await Assert.That(output).Contains(AllHits);
    }

    [Test]
    public async Task ReadOnlyRunStoresNoGraph()
    {
        await Assert.That(Build(Checkout("a"), "--read-only")).Contains(NoneStored);

        await Assert.That(Build(Checkout("b"), "--read-only")).Contains(NoneStored);
    }

    // A checkout that has been built answers differently from a clean one: bin/ is there. It gets a graph of its own.
    [Test]
    public async Task BuiltCheckoutGetsAGraphOfItsOwn()
    {
        var root = Checkout("a");
        Build(root);

        var second = Build(root);
        await Assert.That(second).Contains("Graph evaluated: ");
        await Assert.That(second).Contains(AllHits);

        var third = Build(root);
        await Assert.That(third).Contains(Stored);
        await Assert.That(third).Contains(AllHits);

        // And the clean clone still finds the first one.
        await Assert.That(Build(Checkout("b"))).Contains(Stored);
    }

    [Test]
    public async Task GraphThatCannotBeReadIsEvaluatedAgain()
    {
        Build(Checkout("a"));
        foreach (var graph in Directory.GetFiles(Path.Combine(temp, "cache", "v1", "graph")))
        {
            File.WriteAllText(graph, "not a graph");
        }

        var output = Build(Checkout("b"));

        await Assert.That(output).Contains("Graph evaluated: the stored graph could not be used");
        await Assert.That(output).Contains(AllHits);
    }

    // A read of the disk that asks no one leaves no question behind, so nothing could tell when its answer changes.
    [Test]
    public async Task ProjectFileThatReadsTheDiskItselfStoresNoGraph()
    {
        string WithRead(string root)
        {
            Edit(root, "Directory.Build.props", "</Project>", """
                <PropertyGroup><Stamped>$([System.IO.File]::Exists('$(MSBuildThisFileDirectory)stamp.txt'))</Stamped></PropertyGroup>
                </Project>
                """);
            return root;
        }

        var first = Build(WithRead(Checkout("a")));
        await Assert.That(first).Contains("Not stored: ");
        await Assert.That(first).Contains("Directory.Build.props");

        var clone = Build(WithRead(Checkout("b")));
        await Assert.That(clone).Contains(NoneStored);
        await Assert.That(clone).Contains(AllHits);
    }
}
