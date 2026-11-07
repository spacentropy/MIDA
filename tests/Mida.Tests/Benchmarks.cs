using System.Diagnostics;
using System.Runtime.InteropServices;
using static Mida.Tests.TestRepo;

namespace Mida.Tests;

/// <summary>
/// Wall-clock runs of the scenarios in docs/notes/benchmarks.md. Never part of the suite:
/// dotnet test --project tests/Mida.Tests -f net10.0 --treenode-filter "/*/*/Benchmarks/Build128" --output Detailed
/// </summary>
[Explicit]
[NotInParallel]
public sealed class Benchmarks : IDisposable
{
    private readonly string temp = Directory.CreateTempSubdirectory("mida-bench-").FullName;
    private readonly List<string> rows = [];

    public void Dispose() => Directory.Delete(temp, recursive: true);

    [Test]
    public Task Build8() => Build(8);

    [Test]
    public Task Build128() => Build(128);

    [Test]
    public Task Build1024() => Build(1024);

    private async Task Build(int count)
    {
        // shortcut: multi-targeting off until plan 4b item 1 lands, then pass multiTargetEvery: 5.
        GeneratedRepo Generate(string name) => GeneratedRepo.Generate(Path.Combine(temp, name), count);
        var cache = Path.Combine(temp, "cache");
        string Mida(GeneratedRepo repo) => Dotnet(repo.Root, null, Path.Combine(AppContext.BaseDirectory, "Mida.dll"), "build", "Repo.slnx", "--cache", cache);

        var baseline = Generate("baseline");
        Measure("dotnet build, cold", () => Dotnet(baseline.Root, null, "build", "Repo.slnx"));

        var writer = Generate("a");
        Measure("mida build, empty cache", () => Mida(writer));

        var clone = Generate("some/deeper/path/b");
        Measure("mida build, clean clone", () => Mida(clone));

        var leaf = clone.Libraries.Where(p => p.Layer == 0).MaxBy(p => clone.Dependents(p).Count)!;
        var dependents = clone.Dependents(leaf).Count;
        clone.BodyEdit(leaf);
        Measure($"mida build, body edit of {leaf.Name} ({dependents} dependents)", () => Mida(clone));
        clone.ApiEdit(leaf);
        Measure($"mida build, API edit of {leaf.Name} ({dependents} dependents)", () => Mida(clone));

        var megabytes = Directory.EnumerateFiles(cache, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) / 1_000_000;
        var table = $"""
            {count} projects, cache {megabytes} MB, {RuntimeInformation.OSDescription} {RuntimeInformation.OSArchitecture}

            | scenario | s | result |
            |---|---|---|
            {string.Join('\n', rows)}

            """;
        var path = Path.Combine(TestContext.ResultsDirectory, $"bench-{count}.md");
        await File.WriteAllTextAsync(path, table);
        TestContext.Current!.Output.WriteLine($"{path}\n{table}");
    }

    private void Measure(string scenario, Func<string> run)
    {
        var watch = Stopwatch.StartNew();
        var output = run();
        var result = string.Join("; ", output.Split('\n').Where(l => l.Contains(" keys in ", StringComparison.Ordinal) || l.Contains(" hits, ", StringComparison.Ordinal)).Select(l => l.Trim()));
        rows.Add($"| {scenario} | {watch.Elapsed.TotalSeconds:F1} | {result} |");
    }
}
