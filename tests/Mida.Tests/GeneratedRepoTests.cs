using System.Globalization;
using System.Text.RegularExpressions;
using static Mida.Tests.TestRepo;

namespace Mida.Tests;

/// <summary>The benchmark scenarios on the 8-project generated repo, with exact hit counts and exact failing tests from the graph.</summary>
public sealed partial class GeneratedRepoTests : IDisposable
{
    private const int Count = 8;

    private readonly string temp = Directory.CreateTempSubdirectory("mida-tests-").FullName;

    public void Dispose() => Directory.Delete(temp, recursive: true);

    private GeneratedRepo Generate(string relativePath) => GeneratedRepo.Generate(Path.Combine(temp, relativePath), Count);

    private string Build(GeneratedRepo repo) =>
        Dotnet(repo.Root, null, Path.Combine(AppContext.BaseDirectory, "Mida.dll"), "build", "Repo.slnx", "--cache", Path.Combine(temp, "cache"));

    private (int ExitCode, string Output) Test(GeneratedRepo repo, params string[] arguments) =>
        Run(repo.Root, null, [Path.Combine(AppContext.BaseDirectory, "Mida.dll"), "test", "Repo.slnx", "--cache", Path.Combine(temp, "cache"), .. arguments]);

    [Test]
    public async Task UnchangedCleanCloneRunsNoTestProject()
    {
        var repo = Generate("a");
        var tests = repo.Projects.Count(p => p.IsTest);
        var (exitCode, output) = Test(repo);
        await Assert.That(output).Contains(TestSummary(tests, 0, 0));
        await Assert.That(exitCode).IsEqualTo(0).Because(output);

        (exitCode, output) = Test(Generate("some deeper/path (b)"));
        await Assert.That(output).Contains($"{Count} hits, 0 misses");
        await Assert.That(output).Contains(TestSummary(0, 0, tests));
        await Assert.That(exitCode).IsEqualTo(0).Because(output);
    }

    [Test]
    [Arguments("body")]
    [Arguments("api")]
    public async Task EditRunsExactlyTheTestProjectsAboveIt(string kind)
    {
        var repo = Generate("a");
        var (exitCode, output) = Test(repo);
        await Assert.That(exitCode).IsEqualTo(0).Because(output);

        // A library with a test above it and a test beside it, so running everything would not pass for running the right ones.
        var tests = repo.Projects.Where(p => p.IsTest).ToList();
        var project = repo.Libraries.Where(p => repo.Dependents(p).Any(d => d.IsTest)).MinBy(p => repo.Dependents(p).Count(d => d.IsTest))!;
        var above = repo.Dependents(project).Where(p => p.IsTest).ToList();
        await Assert.That(above.Count).IsLessThan(tests.Count);

        if (kind == "body")
        {
            // The value the tests above compare changes, so each of them fails, and a failure is not stored: it runs again.
            repo.BodyEdit(project);
            foreach (var attempt in new[] { "first", "again" })
            {
                (exitCode, output) = Test(repo);
                await Assert.That(output).Contains(TestSummary(above.Count, above.Count, tests.Count - above.Count)).Because(attempt);
                await Assert.That(exitCode).IsEqualTo(2).Because(output);
                foreach (var test in tests)
                {
                    await Assert.That(output.Contains($"mida: {test.Name}/{test.Name}.csproj failed", StringComparison.Ordinal)).IsEqualTo(above.Contains(test)).Because(output);
                }
            }
        }
        else
        {
            // Nothing a test compares changes, and still each test above ran once on the new code.
            repo.ApiEdit(project);
            (exitCode, output) = Test(repo);
            await Assert.That(output).Contains(TestSummary(above.Count, 0, tests.Count - above.Count));
            await Assert.That(exitCode).IsEqualTo(0).Because(output);
            foreach (var test in tests)
            {
                await Assert.That(output.Contains($"mida: {test.Name}/{test.Name}.csproj passed", StringComparison.Ordinal)).IsEqualTo(above.Contains(test)).Because(output);
            }

            (_, output) = Test(repo);
            await Assert.That(output).Contains(TestSummary(0, 0, tests.Count));
        }
    }

    [Test]
    public async Task StoredPassCountsOnlyForTheSameRunnerArgumentsAndIsNotWrittenReadOnly()
    {
        var repo = Generate("a");
        var tests = repo.Projects.Count(p => p.IsTest);

        // The second run would find what the first stored, had it stored anything.
        await Assert.That(Test(repo, "--read-only").Output).Contains(TestSummary(tests, 0, 0));
        await Assert.That(Test(repo).Output).Contains(TestSummary(tests, 0, 0));
        await Assert.That(Test(repo).Output).Contains(TestSummary(0, 0, tests));

        // What follows -- reaches the runner and is in the key.
        await Assert.That(Test(repo, "--", "--no-ansi").Output).Contains(TestSummary(tests, 0, 0));
        await Assert.That(Test(repo, "--", "--no-ansi").Output).Contains(TestSummary(0, 0, tests));

        await Assert.That(Test(repo, "--all").Output).Contains(TestSummary(tests, 0, 0));

        // A runner that takes no such argument fails, which is how a test project that did not run must read.
        var (exitCode, output) = Test(repo, "--", "--no-such-option");
        await Assert.That(output).Contains(TestSummary(tests, tests, 0));
        await Assert.That(exitCode).IsNotEqualTo(0);
    }

    [Test]
    public async Task FilterRunsOnlyTheSelectedTestAndBuildsOnlyWhatItNeeds()
    {
        var repo = Generate("a");
        var tests = repo.Projects.Where(p => p.IsTest).ToList();
        var test = tests[0];
        var needed = Needed(repo, test).Count;
        await Assert.That(needed).IsLessThan(Count);
        string[] filter = ["--include", $"*/{test.Name}.csproj"];

        var (exitCode, output) = Test(repo, filter);
        await Assert.That(output).Contains($"0 hits, {needed} misses");
        await Assert.That(output).Contains(TestSummary(1, 0, 0));
        await Assert.That(output).Contains($"mida: {test.Name}/{test.Name}.csproj passed");
        await Assert.That(output).DoesNotContain("evaluated again");
        await Assert.That(exitCode).IsEqualTo(0).Because(output);

        // What the filtered run stored is what a whole run looks for, and the other way round: the keys do not depend on the filter.
        (_, output) = Test(repo);
        await Assert.That(output).Contains($"{needed} hits, {Count - needed} misses");
        await Assert.That(output).Contains(TestSummary(tests.Count - 1, 0, 1));

        var other = Path.Combine(temp, "other");
        await Assert.That(Test(Generate("b"), "--cache", other).Output).Contains($"0 hits, {Count} misses");
        (exitCode, output) = Test(Generate("c"), ["--cache", other, .. filter]);
        await Assert.That(output).Contains($"{needed} hits, 0 misses");
        await Assert.That(output).Contains(TestSummary(0, 0, 1));
        await Assert.That(exitCode).IsEqualTo(0).Because(output);

        // Everything else, and nothing of it built again.
        (_, output) = Test(repo, "--exclude", $"*/{test.Name}.csproj");
        await Assert.That(output).Contains(TestSummary(0, 0, tests.Count - 1));
    }

    [Test]
    public async Task ProjectPropertiesChooseCategoriesAndWhichPassesAreStored()
    {
        var repo = Generate("a");
        var tests = repo.Projects.Where(p => p.IsTest).ToList();
        var (stored, slow) = (tests[0], tests[1]);

        // Opt-in: no test project stores a pass but the one that says so.
        Write(repo.Root, "Directory.Build.props", $"""
            <Project>
              <PropertyGroup>
                <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
                <MidaStoreTestPasses>false</MidaStoreTestPasses>
                <MidaStoreTestPasses Condition="'$(MSBuildProjectName)' == '{stored.Name}'">true</MidaStoreTestPasses>
                <MidaTestCategory Condition="'$(MSBuildProjectName)' == '{slow.Name}'">slow; db</MidaTestCategory>
              </PropertyGroup>
            </Project>
            """);
        await Assert.That(Test(repo).Output).Contains(TestSummary(tests.Count, 0, 0));
        await Assert.That(Test(repo).Output).Contains(TestSummary(tests.Count - 1, 0, 1));

        var (exitCode, output) = Test(repo, "--category", "DB");
        await Assert.That(output).Contains(TestSummary(1, 0, 0));
        await Assert.That(output).Contains($"mida: {slow.Name}/{slow.Name}.csproj passed");
        await Assert.That(exitCode).IsEqualTo(0).Because(output);

        // A filter that selects nothing fails, so a typo cannot pass for a green run.
        (exitCode, output) = Test(repo, "--category", "none");
        await Assert.That(output).Contains("select no test project");
        await Assert.That(output).DoesNotContain("test projects run");
        await Assert.That(exitCode).IsEqualTo(1);
    }

    /// <summary>
    /// A project outside the chosen closure that asks a library inside it for one more target, as a web app asks a Razor
    /// library for its static web assets. In a filtered run that library is asked for fewer targets, so it has another
    /// key: the second filtered run must still hit it. With multiTargetEvery, multi-targeted nodes go through the smaller graph too.
    /// </summary>
    [Test]
    [Arguments(0)]
    [Arguments(3)]
    public async Task FilteredRunHitsWhereOnlyAProjectOutsideItAddsTargets(int multiTargetEvery)
    {
        // net10.0;net11.0 needs SDK 11, which only the net11.0 host loads.
        Skip.When(multiTargetEvery > 0 && Environment.Version.Major < 11, "Multi-targeted projects need SDK 11.");
        const int Projects = 16;
        var repo = GeneratedRepo.Generate(Path.Combine(temp, "a"), Projects, multiTargetEvery: multiTargetEvery);
        var (test, needed, asker) = repo.Projects.Where(p => p.IsTest)
            .Select(t => (Test: t, Needed: Needed(repo, t)))
            .Where(t => multiTargetEvery == 0 || t.Needed.Any(n => repo.Projects.First(p => p.Name == n).Frameworks.Contains(';', StringComparison.Ordinal)))
            .Select(t => (t.Test, t.Needed, Asker: repo.Projects.FirstOrDefault(p => !t.Needed.Contains(p.Name) && p.References.Any(t.Needed.Contains))))
            .First(t => t.Asker is not null);
        Write(repo.Root, "Directory.Build.targets", $"""
            <Project>
              <Target Name="MidaExtra" />
              <ItemGroup Condition="'$(MSBuildProjectName)' == '{asker!.Name}'">
                <ProjectReferenceTargets Include="Build" Targets="MidaExtra" />
              </ItemGroup>
            </Project>
            """);
        string[] filter = ["--include", $"*/{test.Name}.csproj"];

        var (exitCode, output) = Test(repo, filter);
        await Assert.That(exitCode).IsEqualTo(0).Because(output);
        var misses = int.Parse(HitsLine().Match(output).Groups[2].Value, CultureInfo.InvariantCulture);

        var clone = GeneratedRepo.Generate(Path.Combine(temp, "b"), Projects, multiTargetEvery: multiTargetEvery);
        Write(clone.Root, "Directory.Build.targets", File.ReadAllText(Path.Combine(repo.Root, "Directory.Build.targets")));
        (exitCode, output) = Test(clone, filter);
        await Assert.That(output).Contains($"{misses} hits, 0 misses");
        await Assert.That(output).Contains(TestSummary(0, 0, 1));
        await Assert.That(output).DoesNotContain("evaluated again");
        await Assert.That(exitCode).IsEqualTo(0).Because(output);

        // A whole run asks those libraries for the extra target, so their filtered entries, built without it, must not
        // be their hits: on OrchardCore that target would be the static web assets'. Everything else in the closure hits.
        var extra = asker.References.Count(r => needed.Contains(r) && !repo.Projects.First(p => p.Name == r).Frameworks.Contains(';', StringComparison.Ordinal));
        if (multiTargetEvery == 0)
        {
            await Assert.That(extra).IsGreaterThan(0);
        }

        var whole = GeneratedRepo.Generate(Path.Combine(temp, "c"), Projects, multiTargetEvery: multiTargetEvery);
        Write(whole.Root, "Directory.Build.targets", File.ReadAllText(Path.Combine(repo.Root, "Directory.Build.targets")));
        output = Build(whole);
        await Assert.That(output).Contains($"{misses - extra} hits, ");
    }

    [GeneratedRegex(@"(\d+) hits, (\d+) misses")]
    private static partial Regex HitsLine();

    /// <summary>What a test project needs built: itself and everything it references, transitively.</summary>
    private static HashSet<string> Needed(GeneratedRepo repo, GeneratedRepo.Project test)
    {
        var byName = repo.Projects.ToDictionary(p => p.Name);
        var needed = new HashSet<string>();
        var pending = new Stack<string>([test.Name]);
        while (pending.TryPop(out var name))
        {
            if (needed.Add(name))
            {
                foreach (var reference in byName[name].References)
                {
                    pending.Push(reference);
                }
            }
        }

        return needed;
    }

    private static async Task AssertTestsFail(GeneratedRepo repo, HashSet<GeneratedRepo.Project> failing)
    {
        foreach (var test in repo.Projects.Where(p => p.IsTest))
        {
            var (exitCode, output) = Run(repo.Root, null, "test", "--project", $"{test.Name}/{test.Name}.csproj", "--no-build");
            await Assert.That(exitCode != 0).IsEqualTo(failing.Contains(test)).Because($"{test.Name}: {output}");
        }
    }

    [Test]
    public async Task CleanCloneAtAnotherPathIsAllHitsAndItsTestsPass()
    {
        await Assert.That(Build(Generate("a"))).Contains($"0 hits, {Count} misses");

        var clone = Generate("some deeper/path (b)");
        var output = Build(clone);
        await Assert.That(output).Contains($"{Count} hits, 0 misses");

        // The tests below run on restore outputs that came from the store, not from a restore.
        await Assert.That(output).Contains("Graph from the store, restore skipped");
        await AssertTestsFail(clone, []);
    }

    [Test]
    [Arguments("body", "leaf")]
    [Arguments("body", "mid")]
    [Arguments("body", "root")]
    [Arguments("api", "leaf")]
    [Arguments("api", "mid")]
    [Arguments("api", "root")]
    public async Task EditMissesExactlyTheExpectedProjectsAndReachesTheirTests(string kind, string position)
    {
        var repo = Generate("a");
        Build(repo);
        var top = repo.Libraries.Max(p => p.Layer);
        var project = position switch
        {
            "leaf" => repo.Libraries.Where(p => p.Layer == 0).MaxBy(p => repo.Dependents(p).Count)!,
            "mid" => repo.Libraries.First(p => p.Layer == top / 2),
            _ => repo.Libraries.First(p => p.Layer == top),
        };
        var dependents = repo.Dependents(project);

        int misses;
        HashSet<GeneratedRepo.Project> failing;
        if (kind == "body")
        {
            repo.BodyEdit(project);
            misses = 1;
            failing = [.. dependents.Where(p => p.IsTest)];
        }
        else
        {
            repo.ApiEdit(project);
            misses = dependents.Count;
            failing = [];
        }

        await Assert.That(Build(repo)).Contains($"{Count - misses} hits, {misses} misses").Because($"{kind} edit of {project.Name}");
        await AssertTestsFail(repo, failing);
    }
}
