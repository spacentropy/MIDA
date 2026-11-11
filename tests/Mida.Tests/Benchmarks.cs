using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static Mida.Tests.TestRepo;

namespace Mida.Tests;

/// <summary>
/// Wall-clock runs of the scenarios in docs/notes/benchmarks.md, one per test, each beside a plain dotnet build of the
/// same repo on the same machine. Never part of the suite:
/// dotnet test --project tests/Mida.Tests -f net10.0 --treenode-filter "/*/*/Benchmarks/Unchanged" --output Detailed
/// MIDA_BENCH_COUNT is the number of projects, 128 unless set. .github/workflows/bench.yml runs them on the packed tool.
/// </summary>
[Explicit]
[NotInParallel]
public sealed class Benchmarks : IDisposable
{
    // The fastest of these counts: between single runs of one commit the ratio moved by 7%.
    private const int Repeats = 3;

    private const string UnknownCpu = "unknown CPU";

    private static readonly int Count = int.Parse(Environment.GetEnvironmentVariable("MIDA_BENCH_COUNT") ?? "128", CultureInfo.InvariantCulture);

    private readonly string temp = Temp();

    public void Dispose() => Directory.Delete(temp, recursive: true);

    [Test]
    public Task Fresh() => Measure(filled: false, _ => Count);

    [Test]
    public Task Unchanged() => Measure(filled: true, _ => 0);

    [Test]
    public Task BodyEdit() => Measure(filled: true, clone =>
    {
        clone.BodyEdit(WidestLeaf(clone));
        return 1;
    });

    [Test]
    public Task ApiEdit() => Measure(filled: true, clone =>
    {
        var leaf = WidestLeaf(clone);
        clone.ApiEdit(leaf);
        return clone.Dependents(leaf).Count;
    });

    private static GeneratedRepo.Project WidestLeaf(GeneratedRepo repo) =>
        repo.Libraries.Where(p => p.Layer == 0).MaxBy(p => repo.Dependents(p).Count)!;

    // shortcut: multi-targeting off, since net10.0;net11.0 needs SDK 11, which only the net11.0 host loads, and the
    // numbers are on SDK 10. Pass multiTargetEvery: 5 when the benchmark moves to -f net11.0.
    private GeneratedRepo Generate(string name) => GeneratedRepo.Generate(Path.Combine(temp, name), Count);

    // CI names the packed tool it installed, which is what users run. Without one, the build beside the tests runs.
    private static string Mida(GeneratedRepo repo, string cache)
    {
        string[] build = ["build", "Repo.slnx", "--cache", cache];
        var (exitCode, output) = Environment.GetEnvironmentVariable("MIDA_BENCH_TOOL") is { Length: > 0 } tool
            ? Start(tool, repo.Root, null, build)
            : Run(repo.Root, null, [Path.Combine(AppContext.BaseDirectory, "Mida.dll"), .. build]);
        if (exitCode != 0)
        {
            Assert.Fail(output);
        }

        return output;
    }

    /// <summary>Times a clean clone, which <paramref name="edit"/> changes and says how many projects must then miss.</summary>
    private async Task Measure(bool filled, Func<GeneratedRepo, int> edit, [CallerMemberName] string scenario = "")
    {
        // Package downloads and dotnet's first-run setup are not what is measured, so a small repo pays for them first.
        Dotnet(GeneratedRepo.Generate(Path.Combine(temp, "prime"), 8).Root, null, "restore", "Repo.slnx");

        // What an earlier CI run left: the cache a runner restores before it builds its own clean clone at another path.
        var filledCache = Path.Combine(temp, "cache");
        // A built checkout goes as soon as it is timed: at 1024 projects a hosted runner's disk held six of them, not seven.
        if (filled)
        {
            var writer = Generate("a");
            Mida(writer, filledCache);
            Directory.Delete(writer.Root, recursive: true);
        }

        var (plain, seconds, output, cache) = (double.MaxValue, double.MaxValue, string.Empty, filledCache);
        var runs = new List<string>();
        for (var i = 0; i < Repeats; i++)
        {
            // The same repo without mida, on the same machine: seconds differ between machines, the ratio to this far less.
            var baseline = Generate("baseline");
            var (plainSeconds, plainLoad) = Time(() => Dotnet(baseline.Root, null, "build", "Repo.slnx"), out _);
            Directory.Delete(baseline.Root, recursive: true);

            // Its own copy of the cache, since a run with misses adds to it.
            cache = Path.Combine(temp, $"cache{i}");
            Copy(filledCache, cache);
            var clone = Generate("some/deeper/path/b");
            var misses = edit(clone);
            var (midaSeconds, midaLoad) = Time(() => Mida(clone, cache), out output);

            // A run that hits or misses the wrong projects has no time worth comparing.
            await Assert.That(output).Contains($"{Count - misses} hits, {misses} misses");
            Directory.Delete(clone.Root, recursive: true);

            (plain, seconds) = (Math.Min(plain, plainSeconds), Math.Min(seconds, midaSeconds));
            runs.Add($"| {i + 1} | {plainSeconds:F1} s | {plainLoad} | {midaSeconds:F1} s | {midaLoad} |");
        }

        var ratio = seconds / plain;
        var cpu = Cpu();
        var path = Path.Combine(TestContext.ResultsDirectory, $"bench-{scenario}-{Count}");

        // CI puts the last run's files here. Unset, the directory is one that holds none.
        var previous = Path.Combine(Environment.GetEnvironmentVariable("MIDA_BENCH_PREVIOUS") ?? temp, Path.GetFileName(path));
        var change = "none";
        if (File.Exists(previous + ".ratio"))
        {
            var last = double.Parse(await File.ReadAllTextAsync(previous + ".ratio"), CultureInfo.InvariantCulture);

            // A ratio from other hardware says nothing about the commit, so that hardware is named: on another CPU model
            // with a disk half as fast, ApiEdit moved by 18% with mida unchanged.
            var lastCpu = File.Exists(previous + ".cpu") ? await File.ReadAllTextAsync(previous + ".cpu") : UnknownCpu;
            change = $"{last:F2} ({(ratio - last) / last:+0%;-0%}){(lastCpu == cpu ? string.Empty : $", on {lastCpu}")}";
        }

        var megabytes = Directory.EnumerateFiles(cache, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) / 1_000_000;
        var result = string.Join("; ", output.Split('\n').Where(l => l.Contains(" keys in ", StringComparison.Ordinal) || l.Contains(" hits, ", StringComparison.Ordinal)).Select(l => l.Trim()));
        var table = $"""
            | scenario | projects | dotnet build | mida build | mida / dotnet | last run | result |
            |---|---|---|---|---|---|---|
            | {scenario} | {Count} | {plain:F1} s | {seconds:F1} s | {ratio:F2} | {change} | {result} |

            Fastest of {Repeats}, cache {megabytes} MB, {RuntimeInformation.OSDescription} {RuntimeInformation.OSArchitecture}, {Environment.ProcessorCount} processors, {cpu}

            | run | dotnet build | machine meanwhile | mida build | machine meanwhile |
            |---|---|---|---|---|
            {string.Join('\n', runs)}

            """;
        await File.WriteAllTextAsync(path + ".md", table);
        await File.WriteAllTextAsync(path + ".ratio", ratio.ToString("F3", CultureInfo.InvariantCulture));
        await File.WriteAllTextAsync(path + ".cpu", cpu);
        TestContext.Current!.Output.WriteLine($"{path}.md\n{table}");
    }

    private (double Seconds, string Load) Time(Func<string> run, out string output)
    {
        // A CI runner starts with no build servers, and a compiler server left by the run before would flatter this one.
        Run(temp, null, "build-server", "shutdown");
        var before = CpuTicks();
        var watch = Stopwatch.StartNew();
        output = run();
        return (watch.Elapsed.TotalSeconds, Load(before));
    }

    /// <summary>
    /// What the whole machine did since <paramref name="before"/>, which tells a slow disk or a crowded host from slow
    /// code: on hosted runners one commit's Fresh ratio moved by 23%. Empty off Linux.
    /// </summary>
    private static string Load(long[] before)
    {
        if (before.Length == 0)
        {
            return string.Empty;
        }

        // Of the numbers on the first line of /proc/stat, the fourth, fifth and eighth are idle, iowait and steal.
        var ticks = CpuTicks().Zip(before, (now, was) => now - was).ToArray();
        double total = ticks.Sum();
        var available = File.ReadLines("/proc/meminfo").First(l => l.StartsWith("MemAvailable:", StringComparison.Ordinal));
        var gigabytes = long.Parse(available.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1], CultureInfo.InvariantCulture) / 1048576.0;
        return $"{100 * (total - ticks[3] - ticks[4]) / total:F0}% busy, {100 * ticks[4] / total:F0}% iowait, {100 * ticks[7] / total:F0}% steal, {gigabytes:F1} GB available after";
    }

    private static long[] CpuTicks() => File.Exists("/proc/stat")
        ? [.. File.ReadLines("/proc/stat").First().Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Take(8).Select(t => long.Parse(t, CultureInfo.InvariantCulture))]
        : [];

    // Only Linux on x64 names the model there.
    private static string Cpu() =>
        (File.Exists("/proc/cpuinfo") ? File.ReadLines("/proc/cpuinfo").FirstOrDefault(l => l.StartsWith("model name", StringComparison.Ordinal)) : null)
            ?.Split(':', 2)[1].Trim() ?? UnknownCpu;

    private static void Copy(string source, string destination)
    {
        foreach (var file in Directory.Exists(source) ? Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories) : [])
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }
}
