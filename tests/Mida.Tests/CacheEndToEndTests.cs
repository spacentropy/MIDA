using static Mida.Tests.TestRepo;

namespace Mida.Tests;

/// <summary>Real builds and real test runs. The Tests project hits on a library's body edit, which is the stale-copy trap.</summary>
public sealed class CacheEndToEndTests : IDisposable
{
    private const string AllHits = "5 hits, 0 misses";

    private readonly string temp = Directory.CreateTempSubdirectory("mida-tests-").FullName;

    public void Dispose() => Directory.Delete(temp, recursive: true);

    private string Checkout(string relativePath)
    {
        var root = TestRepo.Checkout(Path.Combine(temp, relativePath));
        Write(root, "Repo.slnx", Solution("Tests"));
        Write(root, "global.json", """{ "test": { "runner": "Microsoft.Testing.Platform" } }""");
        Write(root, "Directory.Packages.props", """
            <Project>
              <ItemGroup>
                <PackageVersion Include="Newtonsoft.Json" Version="13.0.3" />
                <PackageVersion Include="TUnit" Version="1.81.5" />
              </ItemGroup>
            </Project>
            """);
        Write(root, "LibB/LibB.csproj", Project("""
            <PackageReference Include="Newtonsoft.Json" />
            <None Update="data.txt" CopyToOutputDirectory="PreserveNewest" />
            """));
        Write(root, "LibB/data.txt", "data");
        Write(root, "Tests/Tests.csproj", Project(
            Reference("LibA") + """<PackageReference Include="TUnit" />""",
            "<OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings>"));
        Write(root, "Tests/Cover.cs", """
            public class Cover
            {
                [Test]
                public async Task Name() => await Assert.That(LibA.A.Name()).IsEqualTo("b");

                [Test]
                public async Task Data() => await Assert.That(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data.txt"))).IsEqualTo("data");
            }
            """);
        return root;
    }

    private string Build(string root) =>
        Dotnet(root, null, Path.Combine(AppContext.BaseDirectory, "Mida.dll"), "build", "Repo.slnx", "--cache", Path.Combine(temp, "cache"));

    private static (int ExitCode, string Output) RunTests(string root) => Run(root, null, "test", "--project", "Tests/Tests.csproj", "--no-build");

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

    private static async Task AssertTestsPass(string root)
    {
        var (exitCode, output) = RunTests(root);
        await Assert.That(exitCode).IsEqualTo(0).Because(output);
    }

    private static async Task AssertTestsFailOn(string root, string edited)
    {
        var (exitCode, output) = RunTests(root);
        await Assert.That(exitCode).IsNotEqualTo(0).Because(output);
        await Assert.That(output).Contains(edited);
    }

    [Test]
    public async Task CleanCloneAtAnotherPathIsAllHitsAndItsTestsPass()
    {
        await Assert.That(Build(Checkout("a"))).Contains("0 hits, 5 misses");

        var clone = Checkout("some deeper/path (b)");
        await Assert.That(Build(clone)).Contains(AllHits);

        await AssertTestsPass(clone);
    }

    [Test]
    [Arguments("LibB/B.cs", """=> "b";""", """=> "edited";""", "4 hits, 1 misses")]
    [Arguments("LibB/data.txt", "data", "edited", "1 hits, 4 misses")]
    public async Task EditReachesTestsWhenTheLibraryMisses(string file, string find, string replace, string expected)
    {
        var root = Checkout("a");
        Build(root);

        Edit(root, file, find, replace);

        await Assert.That(Build(root)).Contains(expected);
        await AssertTestsFailOn(root, "edited");
    }

    [Test]
    [Arguments("LibB/B.cs", """=> "b";""", """=> "edited";""")]
    [Arguments("LibB/data.txt", "data", "edited")]
    public async Task EditReachesTestsWhenTheLibraryHitsFromANewerEntryThanItsDependents(string file, string find, string replace)
    {
        var writer = Checkout("a");
        Build(writer);
        Edit(writer, file, find, replace);
        Build(writer);

        var clone = Checkout("b");
        Edit(clone, file, find, replace);

        await Assert.That(Build(clone)).Contains(AllHits);
        await AssertTestsFailOn(clone, "edited");
    }

    [Test]
    public async Task HitRemovesOutputsOfAnotherRevisionAndKeepsDependentsHitting()
    {
        var root = Checkout("a");
        Build(root);
        const string Copied = """<None Update="data.txt" CopyToOutputDirectory="PreserveNewest" />""";

        Edit(root, "LibB/LibB.csproj", Copied, Copied + """<None Update="extra.txt" CopyToOutputDirectory="PreserveNewest" />""");
        Write(root, "LibB/extra.txt", "extra");
        Build(root);
        var extra = Path.Combine(root, "Tests", "bin", "Debug", "net10.0", "extra.txt");
        await Assert.That(File.Exists(extra)).IsTrue();

        Edit(root, "LibB/LibB.csproj", """<None Update="extra.txt" CopyToOutputDirectory="PreserveNewest" />""", string.Empty);
        File.Delete(Path.Combine(root, "LibB", "extra.txt"));

        await Assert.That(Build(root)).Contains(AllHits);
        await Assert.That(File.Exists(extra)).IsFalse();
        await Assert.That(File.Exists(Path.Combine(root, "LibB", "bin", "Debug", "net10.0", "extra.txt"))).IsFalse();
        await AssertTestsPass(root);
    }

    [Test]
    public async Task MissDoesNotStoreWhatAHitRestored()
    {
        const string Copied = """<None Update="data.txt" CopyToOutputDirectory="PreserveNewest" />""";
        const string Extra = """<None Update="extra.txt" CopyToOutputDirectory="PreserveNewest" />""";
        void AddExtra(string root)
        {
            Edit(root, "LibB/LibB.csproj", Copied, Copied + Extra);
            Write(root, "LibB/extra.txt", "extra");
        }

        var writer = Checkout("a");
        AddExtra(writer);
        Build(writer);

        var clone = Checkout("b");
        AddExtra(clone);
        await Assert.That(Build(clone)).Contains(AllHits);
        var naming = ManifestsNaming("extra.txt");

        Edit(clone, "LibB/LibB.csproj", Extra, string.Empty);
        File.Delete(Path.Combine(clone, "LibB", "extra.txt"));
        Build(clone);

        await Assert.That(File.Exists(Path.Combine(clone, "Tests", "bin", "Debug", "net10.0", "extra.txt"))).IsFalse();
        await AssertTestsPass(clone);
        await Assert.That(ManifestsNaming("extra.txt")).IsEqualTo(naming);
    }

    private int ManifestsNaming(string file) =>
        Directory.GetFiles(Path.Combine(temp, "cache", "v1", "ac")).Count(manifest => File.ReadAllText(manifest).Contains(file, StringComparison.Ordinal));

    [Test]
    public async Task FilesInAnOutDirOutsideBinAndObjSurvive()
    {
        var root = Path.Combine(temp, "a");
        Write(root, "Lib/Lib.csproj", Project(string.Empty, "<OutDir>out/</OutDir>"));
        Write(root, "Lib/C.cs", "public static class C { }");
        Write(root, "Repo.slnx", """<Solution><Project Path="Lib/Lib.csproj" /></Solution>""");
        var readme = Path.Combine(root, "Lib", "out", "README.md");
        Write(root, "Lib/out/README.md", "kept");

        await Assert.That(Build(root)).Contains("0 hits, 1 misses");
        await Assert.That(File.Exists(readme)).IsTrue();

        Edit(root, "Lib/C.cs", "class C", "class Edited");
        await Assert.That(Build(root)).Contains("0 hits, 1 misses");
        await Assert.That(File.Exists(readme)).IsTrue();
    }

    [Test]
    public async Task ProjectsSharingADirectoryKeepEachOthersOutputs()
    {
        // The reference keeps them ordered: unrelated projects in one directory race on the files both store.
        var root = Path.Combine(temp, "a");
        const string Shared = "<OutDir>../shared/</OutDir>";
        Write(root, "First/First.csproj", Project(string.Empty, Shared));
        Write(root, "First/C.cs", """public static class First { public static string Name() => "first"; }""");
        Write(root, "Second/Second.csproj", Project(Reference("First"), Shared));
        Write(root, "Second/C.cs", """public static class Second { public static string Name() => First.Name() + "second"; }""");
        Write(root, "Repo.slnx", """<Solution><Project Path="First/First.csproj" /><Project Path="Second/Second.csproj" /></Solution>""");
        Build(root);

        // Second misses once more: the directory it reads for First's file names now holds its own outputs.
        await Assert.That(Build(root)).Contains("1 hits, 1 misses");
        await Assert.That(Build(root)).Contains("2 hits, 0 misses");

        // Compiling Second reads First.dll, so its miss must not clear the directory.
        Edit(root, "Second/C.cs", "\"second\"", "\"edited\"");

        await Assert.That(Build(root)).Contains("1 hits, 1 misses");
        await Assert.That(File.Exists(Path.Combine(root, "shared", "First.dll"))).IsTrue();
        await Assert.That(File.Exists(Path.Combine(root, "shared", "Second.dll"))).IsTrue();
    }

    [Test]
    public async Task StaticWebAssetsAreNotStoredAndOtherProjectsAre()
    {
        var clone = WebRepo("a");
        await Assert.That(Build(clone)).Contains("0 hits, 2 misses");

        clone = WebRepo("b");
        await Assert.That(Build(clone)).Contains("1 hits, 1 misses");
        await Assert.That(File.ReadAllText(Path.Combine(clone, "Web", "bin", "Debug", "net10.0", "Web.staticwebassets.runtime.json"))).Contains(clone);
    }

    private string WebRepo(string relativePath)
    {
        var root = Path.Combine(temp, relativePath);
        Write(root, "Repo.slnx", """<Solution><Project Path="Lib/Lib.csproj" /><Project Path="Web/Web.csproj" /></Solution>""");
        Write(root, "Lib/Lib.csproj", Project(string.Empty));
        Write(root, "Lib/C.cs", "public static class C { }");
        Write(root, "Web/Web.csproj", """
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
            </Project>
            """);
        Write(root, "Web/Program.cs", "System.Console.WriteLine();");
        Write(root, "Web/wwwroot/app.css", "body { }");
        return root;
    }

    [Test]
    [Arguments("[]")]
    [Arguments("""[{"name":"Build","items":[{"spec":"","metadata":{}}]}]""")]
    [Arguments("""[{"name":"Build","items":[{"spec":"s","metadata":{"FullPath":"x"}}]}]""")]
    public async Task TargetsMSBuildRejectsAreAMiss(string targets)
    {
        var root = Checkout("a");
        Build(root);
        foreach (var manifest in Directory.GetFiles(Path.Combine(temp, "cache", "v1", "ac")))
        {
            var json = File.ReadAllText(manifest);
            File.WriteAllText(manifest, $$"""{{json[..json.IndexOf("\"targets\":", StringComparison.Ordinal)]}}"targets":{{targets}}}""");
        }

        await Assert.That(Build(root)).Contains("0 hits, 5 misses");
    }

    [Test]
    public async Task MissingBlobIsAMiss()
    {
        Build(Checkout("a"));
        Directory.Delete(Path.Combine(temp, "cache", "v1", "cas"), recursive: true);

        var clone = Checkout("b");
        await Assert.That(Build(clone)).Contains("0 hits, 5 misses");

        await AssertTestsPass(clone);
    }
}
