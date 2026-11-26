using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using static Mida.Tests.TestRepo;

namespace Mida.Tests;

/// <summary>Real builds and real test runs. The Tests project hits on a library's body edit, which is the stale-copy trap.</summary>
public sealed class CacheEndToEndTests : IDisposable
{
    private const string AllHits = "5 hits, 0 misses";

    private readonly string temp = Temp();

    [ClassDataSource<GcsEmulator>(Shared = SharedType.PerTestSession)]
    public required GcsEmulator Emulator { get; init; }

    public void Dispose() => Directory.Delete(temp, recursive: true);

    // Outside git the implementation names the checkout, so netstandard2.0, which has no reference assembly by default, needs one.
    private const string MultiTarget =
        "<TargetFrameworks>net10.0;netstandard2.0</TargetFrameworks><ProduceReferenceAssembly>true</ProduceReferenceAssembly><LangVersion>latest</LangVersion>";

    private string Checkout(string relativePath, bool multiTarget = false)
    {
        var root = TestRepo.Checkout(Path.Combine(temp, relativePath));
        Write(root, "Repo.slnx", Solution("Tests"));
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
            """).Replace("<TargetFramework>net10.0</TargetFramework>", multiTarget ? MultiTarget : "<TargetFramework>net10.0</TargetFramework>", StringComparison.Ordinal));
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

    // No solution: the default finds the only one in the directory.
    private string Build(string root, params string[] arguments) =>
        Dotnet(root, null, [Path.Combine(AppContext.BaseDirectory, "Mida.dll"), "build", "--cache", Path.Combine(temp, "cache"), .. arguments]);

    private static (int ExitCode, string Output) RunTests(string root, params string[] arguments) =>
        Run(root, null, ["test", "--project", "Tests/Tests.csproj", "--no-build", .. arguments]);

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
    [Arguments(false, "0 hits, 5 misses", AllHits)]
    [Arguments(true, "0 hits, 4 misses", "4 hits, 0 misses")]
    public async Task CleanCloneAtAnotherPathIsAllHitsAndItsTestsPass(bool multiTarget, string first, string second)
    {
        await Assert.That(Build(Checkout("a", multiTarget))).Contains(first);

        var clone = Checkout("some deeper/path (b)", multiTarget);
        var output = Build(clone);
        await Assert.That(output).Contains(second);

        // The library's outer node and its two inner nodes, which the counts above leave out.
        if (multiTarget)
        {
            await Assert.That(output).Contains("3 nodes without a key: 3 multi-targeted");
        }
        else
        {
            await Assert.That(output).DoesNotContain("without a key");
        }

        // So the tests run on restore outputs from the store. The multi-targeted library is built on them too.
        await Assert.That(output).Contains("Graph from the store, restore skipped");
        await AssertTestsPass(clone);
    }

    // One framework in TargetFrameworks. Such a project has an outer node and one inner node, and both are cached.
    [Test]
    public async Task OneFrameworkInTargetFrameworksIsCached()
    {
        string Single(string relativePath)
        {
            var root = Checkout(relativePath);
            // LibB is a normal reference of LibA, and Gen its build-time one.
            foreach (var project in new[] { "LibB", "Gen" })
            {
                Edit(root, $"{project}/{project}.csproj", "<TargetFramework>net10.0</TargetFramework>", "<TargetFrameworks>net10.0</TargetFrameworks>");
            }

            return root;
        }

        await Assert.That(Build(Single("a"))).Contains("0 hits, 7 misses");

        var clone = Single("some deeper/path (b)");
        await Assert.That(Build(clone)).Contains("7 hits, 0 misses");
        await AssertTestsPass(clone);

        // A body edit of the build-time reference misses its inner node and all above it: their keys hold that node's key,
        // which the outer node's key does not. The outer node and LibB's two hit.
        Edit(clone, "Gen/G.cs", "\"g\"", "\"edited\"");
        await Assert.That(Build(clone)).Contains("3 hits, 4 misses");

        // A body edit of the library misses its inner node alone and reaches the tests through hits.
        Edit(clone, "LibB/B.cs", """=> "b";""", """=> "edited";""");
        await Assert.That(Build(clone)).Contains("6 hits, 1 misses");
        await AssertTestsFailOn(clone, "edited");
    }

    // A hit writes no package: with TargetFrameworks pack runs in the outer build, which replays target results, and with
    // TargetFramework the package lands in bin/Debug, outside the outputs a hit restores. So no node of LibB has a key.
    [Test]
    [Arguments("<TargetFrameworks>net10.0</TargetFrameworks>", 2)]
    [Arguments("<TargetFramework>net10.0</TargetFramework>", 1)]
    public async Task ProjectThatPacksOnBuildIsBuiltAndKeepsItsPackage(string framework, int nodes)
    {
        string Packing(string relativePath)
        {
            var root = Checkout(relativePath);
            Edit(root, "LibB/LibB.csproj", "<TargetFramework>net10.0</TargetFramework>", $"{framework}<GeneratePackageOnBuild>True</GeneratePackageOnBuild>");
            return root;
        }

        var first = Build(Packing("a"));
        var clone = Packing("some deeper/path (b)");
        var output = Build(clone);

        var packages = Directory.GetFiles(Path.Combine(clone, "LibB", "bin"), "*.nupkg", SearchOption.AllDirectories);
        await Assert.That(packages.Length).IsEqualTo(1).Because(output);
        using (var package = ZipFile.OpenRead(packages[0]))
        {
            using var packed = new MemoryStream();
            await using (var entry = await package.GetEntry("lib/net10.0/LibB.dll")!.OpenAsync())
            {
                await entry.CopyToAsync(packed);
            }

            await Assert.That(packed.ToArray().SequenceEqual(File.ReadAllBytes(Path.Combine(clone, "LibB", "bin", "Debug", "net10.0", "LibB.dll")))).IsTrue();
        }

        await Assert.That(first).Contains("0 hits, 4 misses");
        await Assert.That(output).Contains("4 hits, 0 misses");
        await Assert.That(output).Contains($"{nodes} nodes without a key: {nodes} packs on build");
        await AssertTestsPass(clone);
    }

    // A source file written from a target into obj/Debug/Scripts, outside obj/Debug/net10.0, and copied to bin/. A hit
    // of LibB would leave it out, and LibA, which misses, would then fail to copy it.
    [Test]
    public async Task ProjectThatCopiesAFileItWritesOutsideItsOutputsIsBuilt()
    {
        string Writing(string relativePath)
        {
            var root = Checkout(relativePath);
            // Where such a file lands when a build SDK sets IntermediateOutputPath early, and a property takes it before
            // the .NET SDK appends the framework.
            Edit(root, "LibB/LibB.csproj", "</Project>", """
                  <PropertyGroup><GenDir>$(BaseIntermediateOutputPath)$(Configuration)/Scripts/</GenDir></PropertyGroup>
                  <ItemGroup><Content Include="$(GenDir)Gen.txt" CopyToOutputDirectory="PreserveNewest" Link="Scripts/Gen.txt" /></ItemGroup>
                  <Target Name="WriteGen" BeforeTargets="CoreCompile;GetCopyToOutputDirectoryItems">
                    <WriteLinesToFile File="$(GenDir)Gen.txt" Lines="gen" Overwrite="true" WriteOnlyWhenDifferent="true" />
                  </Target>
                </Project>
                """);
            return root;
        }

        Build(Writing("a"));
        var clone = Writing("some deeper/path (b)");
        Edit(clone, "LibA/A.cs", "=> LibB.B.Name();", "=> LibB.B.Name() + string.Empty;");
        var output = Build(clone);

        await Assert.That(output).Contains("1 nodes without a key: 1 copies a file it writes outside its outputs");
        await Assert.That(File.ReadAllText(Path.Combine(clone, "LibA", "bin", "Debug", "net10.0", "Scripts", "Gen.txt")).Trim()).IsEqualTo("gen");
        await AssertTestsPass(clone);
    }

    [Test]
    public async Task CloneOnAnotherMachineHitsThroughTheBucket()
    {
        var bucket = $"gs://{Emulator.Bucket}/{Emulator.Prefix()}";
        await Assert.That(Build(Checkout("a"), "--cache", bucket)).Contains("0 hits, 5 misses");
        // Replayed target results with package paths would need a {NugetPackageRoot} token. This root exists here, so only this check sees one.
        var root = Environment.GetEnvironmentVariable("NUGET_PACKAGES")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        var manifests = Directory.GetFiles(Path.Combine(temp, "cache", "v2", "ac"));
        await Assert.That(manifests).IsNotEmpty();
        await Assert.That(manifests.Select(File.ReadAllText).Where(m => m.Contains(root, StringComparison.OrdinalIgnoreCase))).IsEmpty();

        // Another machine: its own local tier and its own NuGet root.
        var clone = Checkout("b");
        var packages = Path.Combine(temp, "packages");
        string[] mida = [Path.Combine(AppContext.BaseDirectory, "Mida.dll"), "build", "Repo.slnx"];
        var output = Dotnet(clone, packages, [.. mida, "--cache", Path.Combine(temp, "other cache"), "--cache", bucket]);
        await Assert.That(output).Contains(AllHits);

        var (exitCode, testOutput) = Run(clone, packages, "test", "--project", "Tests/Tests.csproj", "--no-build");
        await Assert.That(exitCode).IsEqualTo(0).Because(testOutput);

        // A third machine with the writer's package folder: the graph and the restore outputs come through the bucket as well.
        var third = Checkout("c");
        output = Dotnet(third, null, [.. mida, "--cache", Path.Combine(temp, "third cache"), "--cache", bucket]);
        await Assert.That(output).Contains("Graph from the store, restore skipped");
        await Assert.That(output).Contains(AllHits);
        await AssertTestsPass(third);
    }

    // What CI does: one runner's pass must spare the next runner the test, though that one starts with an empty local store.
    [Test]
    public async Task TestPassOnAnotherMachineComesThroughTheBucket()
    {
        var bucket = $"gs://{Emulator.Bucket}/{Emulator.Prefix()}";
        string[] mida = [Path.Combine(AppContext.BaseDirectory, "Mida.dll"), "test", "--cache", bucket];
        var output = Dotnet(Checkout("a"), null, [.. mida, "--cache", Path.Combine(temp, "cache")]);
        await Assert.That(output).Contains(TestSummary(1, 0, 0));

        output = Dotnet(Checkout("b"), null, [.. mida, "--cache", Path.Combine(temp, "other cache")]);
        await Assert.That(output).Contains(AllHits);
        await Assert.That(output).Contains(TestSummary(0, 0, 1));
    }

    // A task in mida's process binds to mida's copies of assemblies the SDK also ships, such as System.CodeDom, and fails.
    [Test]
    public async Task NoTargetRunsInMidasProcess()
    {
        var root = Checkout("a");
        Write(root, "Directory.Build.targets", """
            <Project>
              <Target Name="NotInMida" BeforeTargets="CollectPackageReferences;CoreCompile">
                <Error Condition="$([System.Environment]::CommandLine.Contains('Mida.dll'))" Text="Ran in mida's process" />
              </Target>
            </Project>
            """);

        await Assert.That(Build(root)).Contains("0 hits, 5 misses");
    }

    [Test]
    public async Task ReadOnlyRunStoresNothing()
    {
        var root = Checkout("a");
        await Assert.That(Build(root, "--read-only")).Contains("0 hits, 5 misses");

        await Assert.That(Build(root, "--read-only")).Contains("0 hits, 5 misses");
    }

    [Test]
    public async Task ReleaseCloneIsAllHitsAndItsTestsPass()
    {
        await Assert.That(Build(Checkout("a"), "-c", "Release")).Contains("0 hits, 5 misses");

        var clone = Checkout("b");
        await Assert.That(Build(clone, "-c", "Release")).Contains(AllHits);

        var (exitCode, output) = RunTests(clone, "-c", "Release");
        await Assert.That(exitCode).IsEqualTo(0).Because(output);
    }

    [Test]
    [Arguments("LibB/B.cs", """=> "b";""", """=> "edited";""", "4 hits, 1 misses", false)]
    [Arguments("LibB/data.txt", "data", "edited", "1 hits, 4 misses", false)]
    [Arguments("LibB/B.cs", """=> "b";""", """=> "edited";""", "4 hits, 0 misses", true)]
    [Arguments("LibB/data.txt", "data", "edited", "1 hits, 3 misses", true)]
    public async Task EditReachesTestsWhenTheLibraryMisses(string file, string find, string replace, string expected, bool multiTarget)
    {
        var root = Checkout("a", multiTarget);
        Build(root);

        Edit(root, file, find, replace);

        await Assert.That(Build(root)).Contains(expected);
        await AssertTestsFailOn(root, "edited");
    }

    // Stamps versions at build time from a file outside the key, the way version tools do from git.
    private const string StampVersion = """
        <Target Name="StampVersion" BeforeTargets="GetAssemblyVersion">
          <ReadLinesFromFile File="version.txt"><Output TaskParameter="Lines" PropertyName="Stamped" /></ReadLinesFromFile>
          <PropertyGroup>
            <FileVersion>$(Stamped)</FileVersion>
            <InformationalVersion>$(Stamped)-alpha.1</InformationalVersion>
            <AssemblyVersion>$([System.Version]::Parse($(Stamped)).Major).0.0.0</AssemblyVersion>
          </PropertyGroup>
        </Target>
        </Project>
        """;

    [Test]
    [Arguments("1.0.1", "4 hits, 1 misses")]
    // A lower AssemblyVersion than a dependent was built against fails to load, so it must miss.
    [Arguments("2.0.0", "1 hits, 4 misses")]
    public async Task BodyEditUnderAStampedVersionMissesDependentsOnlyOnAnAssemblyVersionChange(string version, string expected)
    {
        var root = Checkout("a");
        Edit(root, "LibB/LibB.csproj", "</Project>", StampVersion);
        Write(root, "LibB/version.txt", "1.0.0");
        Build(root);

        Write(root, "LibB/version.txt", version);
        Edit(root, "LibB/B.cs", """=> "b";""", """=> "edited";""");

        await Assert.That(Build(root)).Contains(expected);
        await AssertTestsFailOn(root, "edited");
    }

    [Test]
    [Arguments("LibB/B.cs", """=> "b";""", """=> "edited";""", false)]
    [Arguments("LibB/data.txt", "data", "edited", false)]
    [Arguments("LibB/B.cs", """=> "b";""", """=> "edited";""", true)]
    [Arguments("LibB/data.txt", "data", "edited", true)]
    public async Task EditReachesTestsWhenTheLibraryHitsFromANewerEntryThanItsDependents(string file, string find, string replace, bool multiTarget)
    {
        var writer = Checkout("a", multiTarget);
        Build(writer);
        Edit(writer, file, find, replace);
        Build(writer);

        var clone = Checkout("b", multiTarget);
        Edit(clone, file, find, replace);

        // A multi-targeted library is never stored, so it rebuilds and its dependents hit.
        await Assert.That(Build(clone)).Contains(multiTarget ? "4 hits, 0 misses" : AllHits);
        await AssertTestsFailOn(clone, "edited");
    }

    // A project's own copy wins over a dependency's at the same path, so an edit to the dependency's must not reach it.
    [Test]
    public async Task OwnCopyIsNotTakenFromADependency()
    {
        string WithOwnData(string root)
        {
            Write(root, "App/App.csproj", Project(Reference("LibA") + """<None Update="data.txt" CopyToOutputDirectory="PreserveNewest" />"""));
            Write(root, "App/data.txt", "data");
            return root;
        }

        Build(WithOwnData(Checkout("a")));

        var clone = WithOwnData(Checkout("b"));
        Edit(clone, "LibB/data.txt", "data", "edited");
        await Assert.That(Build(clone)).Contains("1 hits, 4 misses");

        await Assert.That(File.ReadAllText(Path.Combine(clone, "App", "bin", "Debug", "net10.0", "data.txt"))).IsEqualTo("data");
    }

    // A file a target writes to bin/ is in no dependent's key, so the dependent hits while the dependency's file changes.
    [Test]
    public async Task OwnCopyIsNotTakenFromAFileADependencyGenerates()
    {
        const string Generate = """
            <Target Name="Generate" AfterTargets="Build"><Copy SourceFiles="B.cs" DestinationFiles="$(OutDir)gen.txt" /></Target>
            </Project>
            """;
        string WithGenerated(string root)
        {
            Edit(root, "LibB/LibB.csproj", "</Project>", Generate);
            Write(root, "App/App.csproj", Project(Reference("LibA") + """<None Update="gen.txt" CopyToOutputDirectory="PreserveNewest" />"""));
            File.Copy(Path.Combine(root, "LibB", "B.cs"), Path.Combine(root, "App", "gen.txt"));
            return root;
        }

        Build(WithGenerated(Checkout("a")));

        var clone = WithGenerated(Checkout("b"));
        Edit(clone, "LibB/B.cs", """=> "b";""", """=> "edited";""");
        await Assert.That(Build(clone)).Contains("4 hits, 1 misses");

        await Assert.That(File.ReadAllText(Path.Combine(clone, "App", "bin", "Debug", "net10.0", "gen.txt"))).DoesNotContain("edited");
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
        Directory.GetFiles(Path.Combine(temp, "cache", "v2", "ac")).Count(manifest => File.ReadAllText(manifest).Contains(file, StringComparison.Ordinal));

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

        // No build wrote it, so it is no output: a hit elsewhere would write this checkout's copy over that one's.
        await Assert.That(ManifestsNaming("README.md")).IsEqualTo(0);
    }

    // A test project that starts a web host: it takes its web root only from a wwwroot that exists. With one
    // framework in TargetFrameworks the outer build makes bin/Debug/wwwroot as well, outside every output directory.
    [Test]
    [Arguments("<TargetFramework>net10.0</TargetFramework>", "5 hits", false)]
    [Arguments("<TargetFrameworks>net10.0</TargetFrameworks>", "6 hits", true)]
    public async Task EmptyDirectoryTheBuildMakesIsThereAfterAHit(string framework, string hits, bool outer)
    {
        string Making(string relativePath)
        {
            var root = Checkout(relativePath);
            Edit(root, "Tests/Tests.csproj", "<TargetFramework>net10.0</TargetFramework>", framework);
            Edit(root, "Tests/Tests.csproj", "</Project>", """
                  <Target Name="MakeWebRoot" BeforeTargets="Build"><MakeDir Directories="$(OutputPath)wwwroot" /></Target>
                </Project>
                """);
            return root;
        }

        Build(Making("a"));
        var clone = Making("b");

        await Assert.That(Build(clone)).Contains($"{hits}, 0 misses");
        foreach (var directory in outer ? new[] { "net10.0/wwwroot", "wwwroot" } : ["net10.0/wwwroot"])
        {
            await Assert.That(Directory.Exists(Path.Combine(clone, "Tests", "bin", "Debug", directory))).IsTrue().Because(directory);
        }
    }

    // A web app that copies translations from packages into Localization/ and makes wwwroot/, in its own directory.
    // The second build in the writer finds both there already, and still stores them.
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task BuildThatWritesIntoItsSourceTreeIsStoredOnlyWhenItSaysWhere(bool declared)
    {
        string Writing(string relativePath)
        {
            var root = Checkout(relativePath);
            Edit(root, "LibB/LibB.csproj", "</Project>", $"""
                  <PropertyGroup>{(declared ? "<MidaSourceOutputs>Localization;wwwroot</MidaSourceOutputs>" : string.Empty)}</PropertyGroup>
                  <Target Name="WriteSource" AfterTargets="Build">
                    <MakeDir Directories="$(MSBuildProjectDirectory)/wwwroot" />
                    <Copy SourceFiles="data.txt" DestinationFolder="Localization/fr" SkipUnchangedFiles="true" />
                  </Target>
                </Project>
                """);
            return root;
        }

        var writer = Writing("a");
        var first = Build(writer);
        const string Edited = """=> "edited";""";
        Edit(writer, "LibB/B.cs", """=> "b";""", Edited);
        var output = Build(writer);

        var clone = Writing("b");
        Edit(clone, "LibB/B.cs", """=> "b";""", Edited);
        var hit = Build(clone);

        if (declared)
        {
            await Assert.That(output).DoesNotContain("not stored");
            await Assert.That(hit).Contains("5 hits, 0 misses");
            await Assert.That(File.ReadAllText(Path.Combine(clone, "LibB", "Localization", "fr", "data.txt"))).IsEqualTo("data");
            await Assert.That(Directory.Exists(Path.Combine(clone, "LibB", "wwwroot"))).IsTrue();
        }
        else
        {
            // Only a build that finds neither there yet can tell. The writer's second build finds both and stores, but under
            // a key with the copied file as an input, which the clone does not have yet: it misses and refuses again.
            // macOS names the temporary directory through /private, so only the end of the path is compared.
            await Assert.That(first).Contains($"{Path.Combine("a", "LibB", "LibB.csproj")} not stored: its build wrote");
            await Assert.That(hit).Contains("not stored: its build wrote");
        }
    }

    // A name in MidaSourceOutputs takes its files out of the key, so one that also holds a source must not be written over.
    // A Copy into it makes it a predicted output directory too, which must not change that.
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task HitDoesNotWriteOverAnEditedFileThatMidaSourceOutputsNames(bool copiedInto)
    {
        Build(DeclaringSource("a", copiedInto));
        var clone = DeclaringSource("b", copiedInto);
        Write(clone, "LibB/wwwroot/site.txt", "edited");

        var output = Build(clone);

        await Assert.That(output).Contains("differs from what the entry holds");
        await Assert.That(File.ReadAllText(Path.Combine(clone, "LibB", "wwwroot", "site.txt"))).IsEqualTo("edited");
    }

    // A hit notes what it restored for the next miss to delete, which must leave the source files there alone, also once
    // MidaSourceOutputs no longer names them.
    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task MissAfterAHitKeepsTheSourceFilesThatMidaSourceOutputsNames(bool copiedInto, bool narrowed)
    {
        Build(DeclaringSource("a", copiedInto));
        var clone = DeclaringSource("b", copiedInto);
        await Assert.That(Build(clone)).Contains(AllHits);

        if (narrowed)
        {
            Edit(clone, "LibB/LibB.csproj", "<MidaSourceOutputs>wwwroot<", "<MidaSourceOutputs>wwwroot/data.txt<");
        }
        else
        {
            Edit(clone, "LibB/B.cs", """=> "b";""", """=> "edited";""");
        }

        // A project file is in its dependents' keys too.
        await Assert.That(Build(clone)).Contains(narrowed ? "1 hits, 4 misses" : "4 hits, 1 misses");
        await Assert.That(File.ReadAllText(Path.Combine(clone, "LibB", "wwwroot", "site.txt"))).IsEqualTo("source");
    }

    // A hit removes what the entry before it restored and this one does not hold, which must not take a source file.
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task HitToAnEntryWithoutASourceFileKeepsIt(bool copiedInto)
    {
        var writer = DeclaringSource("a", copiedInto);
        Build(writer);
        Edit(writer, "LibB/B.cs", """=> "b";""", """=> "edited";""");
        File.Delete(Path.Combine(writer, "LibB", "wwwroot", "site.txt"));
        Build(writer);

        var clone = DeclaringSource("b", copiedInto);
        await Assert.That(Build(clone)).Contains(AllHits);
        Edit(clone, "LibB/B.cs", """=> "b";""", """=> "edited";""");

        await Assert.That(Build(clone)).Contains(AllHits);
        await Assert.That(File.ReadAllText(Path.Combine(clone, "LibB", "wwwroot", "site.txt"))).IsEqualTo("source");
    }

    private string DeclaringSource(string relativePath, bool copiedInto)
    {
        var root = Checkout(relativePath);
        Edit(root, "LibB/LibB.csproj", "</Project>", $"""
              <PropertyGroup><MidaSourceOutputs>wwwroot</MidaSourceOutputs></PropertyGroup>
              {(copiedInto ? """<Target Name="CopyIn" AfterTargets="Build"><Copy SourceFiles="data.txt" DestinationFolder="wwwroot" SkipUnchangedFiles="true" /></Target>""" : string.Empty)}
            </Project>
            """);
        Write(root, "LibB/wwwroot/site.txt", "source");
        return root;
    }

    // A local database, a log, a file put there by hand: MSBuild deletes only what it wrote, and so does mida.
    [Test]
    public async Task FileTheAppWroteIntoBinSurvivesMissesAndHits()
    {
        var root = Checkout("a");
        Build(root);
        var database = Path.Combine(root, "Tests", "bin", "Debug", "net10.0", "app.db");
        File.WriteAllText(database, "rows");

        Edit(root, "Tests/Cover.cs", "public class Cover", "public sealed class Cover");
        await Assert.That(Build(root)).Contains("4 hits, 1 misses");
        await Assert.That(File.Exists(database)).IsTrue();

        Edit(root, "Tests/Cover.cs", "public sealed class Cover", "public class Cover");
        await Assert.That(Build(root)).Contains(AllHits);
        await Assert.That(File.Exists(database)).IsTrue();
    }

    // At the repo's root such a project's output directory holds .git, which no hit may write over.
    [Test]
    public async Task OutputDirectoryThatHoldsTheProjectIsNotCached()
    {
        var root = Path.Combine(temp, "a");
        Write(root, "App.csproj", Project(string.Empty, "<OutDir>$(MSBuildProjectDirectory)/</OutDir>"));
        Write(root, "C.cs", "public static class C { }");
        Write(root, ".git/config", "[core]");
        Write(root, "Repo.slnx", """<Solution><Project Path="App.csproj" /></Solution>""");

        var output = Build(root);

        await Assert.That(output).Contains("holds the project itself");
        await Assert.That(output).Contains("0 hits, 0 misses");
        await Assert.That(Directory.Exists(Path.Combine(temp, "cache", "v2", "ac"))).IsFalse();
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

    private const string WebMisses = "0 hits, 3 misses";

    private const string WebHits = "3 hits, 0 misses";

    // Asset labels: a host's own, a library's, a library's scoped CSS, and a package's.
    private const string Site = "site.js";
    private const string Library = "_content/Rcl/a.css";
    private const string Scoped = "_content/Rcl/Rcl.bundle.scp.css";
    private const string Package = "_content/Microsoft.AspNetCore.Components.QuickGrid/Microsoft.AspNetCore.Components.QuickGrid.bundle.scp.css";

    /// <summary>Rcl is a Razor class library, Web its host, which also gets a package's assets, and WebTests runs Web in WebApplicationFactory.</summary>
    private string WebRepo(string relativePath, string rclItems = "")
    {
        var root = Path.Combine(temp, relativePath);
        Write(root, "global.json", GlobalJson);
        Write(root, "Repo.slnx", """<Solution><Project Path="Rcl/Rcl.csproj" /><Project Path="Web/Web.csproj" /><Project Path="WebTests/WebTests.csproj" /></Solution>""");
        Write(root, "Rcl/Rcl.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk.Razor">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup><FrameworkReference Include="Microsoft.AspNetCore.App" />{rclItems}</ItemGroup>
            </Project>
            """);
        Write(root, "Rcl/wwwroot/a.css", "a { color: red }");
        Write(root, "Rcl/Comp.razor", "<p>comp</p>");
        Write(root, "Rcl/Comp.razor.css", "p { color: red }");
        Write(root, "Web/Web.csproj", """
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="../Rcl/Rcl.csproj" />
                <PackageReference Include="Microsoft.AspNetCore.Components.QuickGrid" Version="10.0.0" />
              </ItemGroup>
            </Project>
            """);
        Write(root, "Web/Program.cs", """
            var app = WebApplication.CreateBuilder(args).Build();
            app.MapStaticAssets();
            app.Run();

            public partial class Program { }
            """);
        Write(root, "Web/wwwroot/site.js", "var site = 'red';");
        Write(root, "WebTests/WebTests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="../Web/Web.csproj" />
                <PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" Version="10.0.0" />
                <PackageReference Include="TUnit" Version="1.81.5" />
              </ItemGroup>
            </Project>
            """);
        Write(root, "WebTests/Serve.cs", """
            using Microsoft.AspNetCore.Mvc.Testing;

            public class Serve
            {
                [Test]
                [Arguments("/site.js")]
                [Arguments("/_content/Rcl/a.css")]
                public async Task Asset(string path)
                {
                    using var factory = new WebApplicationFactory<Program>();
                    using var client = factory.CreateClient();
                    await Assert.That(await client.GetStringAsync(path)).Contains("red");
                }
            }
            """);
        return root;
    }

    // Started without a build, as CI starts what it built. Port 0, so tests can run side by side.
    private static async Task Serve(string root, Func<HttpClient, Task> fetch)
    {
        var start = new ProcessStartInfo("dotnet", [Path.Combine("bin", "Debug", "net10.0", "Web.dll"), "--urls", "http://127.0.0.1:0"])
        {
            WorkingDirectory = Path.Combine(root, "Web"),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        using var process = Process.Start(start)!;
        try
        {
            const string Listening = "Now listening on: ";
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync()) is not null && !line.Contains(Listening, StringComparison.Ordinal))
            {
            }

            if (line is null)
            {
                Assert.Fail(await process.StandardError.ReadToEndAsync());
                return;
            }

            using var client = new HttpClient { BaseAddress = new Uri(line[(line.IndexOf(Listening, StringComparison.Ordinal) + Listening.Length)..]) };
            await fetch(client);
        }
        finally
        {
            process.Kill(entireProcessTree: true);
        }
    }

    // As a page links it: by its plain URL, and by the fingerprinted one with the integrity the host's manifest gives it.
    private static async Task AssertServes(HttpClient client, string root, string label, string content)
    {
        var endpoints = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "Web", "bin", "Debug", "net10.0", "Web.staticwebassets.endpoints.json")))!["Endpoints"]!.AsArray();
        static string? Property(JsonNode endpoint, string list, string name) =>
            endpoint[list]!.AsArray().FirstOrDefault(p => (string?)p!["Name"] == name)?["Value"]?.GetValue<string>();
        var fingerprinted = endpoints.First(e => Property(e!, "EndpointProperties", "label") == label && Property(e!, "ResponseHeaders", "Content-Encoding") is null)!;

        foreach (var route in new[] { label, (string)fingerprinted["Route"]! })
        {
            var body = await client.GetByteArrayAsync(new Uri(route, UriKind.Relative));
            await Assert.That(Encoding.UTF8.GetString(body)).Contains(content).Because(route);
            await Assert.That($"sha256-{Convert.ToBase64String(SHA256.HashData(body))}").IsEqualTo(Property(fingerprinted, "EndpointProperties", "integrity")).Because(route);
        }
    }

    private static string PackageRoot(string? packages) => packages
        ?? Environment.GetEnvironmentVariable("NUGET_PACKAGES")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");

    // SDK 11 writes a hot reload script with a new key in it on every Debug build of a web app, and lists it among the
    // app's endpoints. A comment edit builds the app again, and its tests must still hit.
    [Test]
    public async Task WebAppBuiltAgainKeepsItsTestsHitting()
    {
        Build(WebRepo("a"));
        var clone = WebRepo("b");
        File.AppendAllText(Path.Combine(clone, "Web", "Program.cs"), "// edited\n");

        await Assert.That(Build(clone)).Contains("2 hits, 1 misses");
    }

    [Test]
    public async Task WebCleanCloneAtAnotherPathIsAllHitsAndServesItsAssets()
    {
        var writer = WebRepo("a");
        await Assert.That(Build(writer)).Contains(WebMisses);
        Directory.Delete(writer, recursive: true);

        var clone = WebRepo("some deeper/path (b)");
        await Assert.That(Build(clone)).Contains(WebHits);

        var runtime = File.ReadAllText(Path.Combine(clone, "Web", "bin", "Debug", "net10.0", "Web.staticwebassets.runtime.json"));
        await Assert.That(runtime).Contains(clone);
        await Assert.That(runtime).DoesNotContain(writer + Path.DirectorySeparatorChar);
        // Outside git an assembly names its PDB and its sources, so only text is checked.
        var texts = Directory.GetFiles(Path.Combine(temp, "cache", "v1", "cas"), "*", SearchOption.AllDirectories)
            .Select(File.ReadAllBytes).Where(b => !b.Contains((byte)0)).Select(b => Encoding.UTF8.GetString(b)).ToList();
        foreach (var path in new[] { writer + Path.DirectorySeparatorChar, PackageRoot(null) })
        {
            await Assert.That(texts.Where(t => t.Contains(path, StringComparison.Ordinal))).IsEmpty().Because(path);
        }

        await Serve(clone, async client =>
        {
            await AssertServes(client, clone, Site, "red");
            await AssertServes(client, clone, Library, "red");
            await AssertServes(client, clone, Scoped, "red");
            await AssertServes(client, clone, Package, "{");
        });

        var (exitCode, output) = Run(clone, null, "test", "--project", "WebTests/WebTests.csproj", "--no-build");
        await Assert.That(exitCode).IsEqualTo(0).Because(output);

        Dotnet(clone, null, "publish", "Web/Web.csproj", "--no-build", "-c", "Debug", "-o", "published");
        await Assert.That(File.ReadAllText(Path.Combine(clone, "published", "wwwroot", "site.js"))).Contains("red");
        await Assert.That(File.ReadAllText(Path.Combine(clone, "published", "wwwroot", "_content", "Rcl", "a.css"))).Contains("red");
    }

    [Test]
    public async Task WebCloneOnAnotherMachineServesItsPackagesAssets()
    {
        await Assert.That(Build(WebRepo("a"))).Contains(WebMisses);

        var clone = WebRepo("b");
        var packages = Path.Combine(temp, "packages");
        var output = Dotnet(clone, packages, [Path.Combine(AppContext.BaseDirectory, "Mida.dll"), "build", "--cache", Path.Combine(temp, "cache")]);
        await Assert.That(output).Contains(WebHits);

        var runtime = File.ReadAllText(Path.Combine(clone, "Web", "bin", "Debug", "net10.0", "Web.staticwebassets.runtime.json"));
        await Assert.That(runtime).Contains(packages);
        await Assert.That(runtime).DoesNotContain(PackageRoot(null));
        await Serve(clone, client => AssertServes(client, clone, Package, "{"));
    }

    // The library's API and file names stay, so only its assets in the key make its host miss.
    [Test]
    [Arguments("Web/wwwroot/site.js", Site, "1 hits, 2 misses", false)]
    [Arguments("Rcl/wwwroot/a.css", Library, WebMisses, false)]
    [Arguments("Rcl/Comp.razor.css", Scoped, WebMisses, false)]
    [Arguments("Web/wwwroot/site.js", Site, "1 hits, 2 misses", true)]
    [Arguments("Rcl/wwwroot/a.css", Library, WebMisses, true)]
    [Arguments("Rcl/Comp.razor.css", Scoped, WebMisses, true)]
    public async Task EditedAssetIsServedWithItsNewIntegrity(string file, string label, string misses, bool fromNewerEntry)
    {
        var root = WebRepo("a");
        Build(root);
        Edit(root, file, "red", "green");
        await Assert.That(Build(root)).Contains(misses);

        if (fromNewerEntry)
        {
            // The library hits from the entry the build above stored, newer than the host's first one.
            root = WebRepo("b");
            Edit(root, file, "red", "green");
            await Assert.That(Build(root)).Contains(WebHits);
        }

        await Serve(root, client => AssertServes(client, root, label, "green"));
    }

    [Test]
    public async Task StaticWebAssetOutsideEveryRootIsNotStoredAndSaysSo()
    {
        Write(temp, "outside/b.css", "b { }");
        var root = WebRepo("a", """<Content Include="../../outside/b.css" Link="wwwroot/b.css" />""");

        var output = Build(root);

        await Assert.That(output).Contains("Rcl.csproj not stored:");
        await Assert.That(output).Contains("which no token covers");
        // The host's manifests name it too. The test project's copies of them are the host's.
        await Assert.That(Build(root)).Contains("1 hits, 2 misses");
    }

    [Test]
    [Arguments("[]")]
    [Arguments("""[{"name":"Build","items":[{"spec":"","metadata":{}}]}]""")]
    [Arguments("""[{"name":"Build","items":[{"spec":"s","metadata":{"FullPath":"x"}}]}]""")]
    public async Task TargetsMSBuildRejectsAreAMiss(string targets)
    {
        var root = Checkout("a");
        Build(root);
        foreach (var manifest in Directory.GetFiles(Path.Combine(temp, "cache", "v2", "ac")))
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
