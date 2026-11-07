using static Mida.Tests.TestRepo;

namespace Mida.Tests;

/// <summary>The benchmark scenarios on the 8-project generated repo, with exact hit counts and exact failing tests from the graph.</summary>
public sealed class GeneratedRepoTests : IDisposable
{
    private const int Count = 8;

    private readonly string temp = Directory.CreateTempSubdirectory("mida-tests-").FullName;

    public void Dispose() => Directory.Delete(temp, recursive: true);

    private GeneratedRepo Generate(string relativePath) => GeneratedRepo.Generate(Path.Combine(temp, relativePath), Count);

    private string Build(GeneratedRepo repo) =>
        Dotnet(repo.Root, null, Path.Combine(AppContext.BaseDirectory, "Mida.dll"), "build", "Repo.slnx", "--cache", Path.Combine(temp, "cache"));

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
        await Assert.That(Build(clone)).Contains($"{Count} hits, 0 misses");

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
