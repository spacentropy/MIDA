using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static Mida.Tests.TestRepo;

namespace Mida.Tests;

/// <summary>
/// Wall-clock runs of the scenarios in docs/notes/benchmarks.md under BenchmarkDotNet, one per test, each beside a
/// plain dotnet build of the same repo on the same machine. Never part of the suite:
/// dotnet test --project tests/Mida.Tests -f net10.0 --treenode-filter "/*/*/Benchmarks/Unchanged" --output Detailed
/// MIDA_BENCH_COUNT is the number of projects, 128 unless set. .github/workflows/bench.yml runs them on the packed tool.
/// </summary>
[Explicit]
[NotInParallel]
public sealed class Benchmarks : IDisposable
{
    // Between single runs of one commit the ratio moved by 7%. MIDA_BENCH_ITERATIONS sets it.
    internal static readonly int Iterations = int.Parse(Environment.GetEnvironmentVariable("MIDA_BENCH_ITERATIONS") ?? "3", CultureInfo.InvariantCulture);

    private const string UnknownCpu = "unknown CPU";

    private static readonly int Count = int.Parse(Environment.GetEnvironmentVariable("MIDA_BENCH_COUNT") ?? "128", CultureInfo.InvariantCulture);

    private readonly string temp = Temp();

    public void Dispose() => Directory.Delete(temp, recursive: true);

    [Test]
    public Task Fresh() => Measure(filled: false, clone => clone.Projects);

    [Test]
    public Task Unchanged() => Measure(filled: true, _ => []);

    [Test]
    public Task BodyEdit() => Measure(filled: true, clone =>
    {
        var leaf = WidestLeaf(clone);
        clone.BodyEdit(leaf);
        return [leaf];
    });

    [Test]
    public Task ApiEdit() => Measure(filled: true, EditApi);

    // mida test beside dotnet test, which builds as well. BodyEdit's tests fail, and a failed run has no time worth
    // comparing, so TestBodyEdit changes a body without changing what it returns.
    [Test]
    public Task TestFresh() => Measure(filled: false, clone => clone.Projects, "test");

    [Test]
    public Task TestUnchanged() => Measure(filled: true, _ => [], "test");

    [Test]
    public Task TestBodyEdit() => Measure(filled: true, clone =>
    {
        var leaf = WidestLeaf(clone);
        clone.RefactorEdit(leaf);
        return [leaf];
    }, "test");

    [Test]
    public Task TestApiEdit() => Measure(filled: true, EditApi, "test");

    private static HashSet<GeneratedRepo.Project> EditApi(GeneratedRepo clone)
    {
        var leaf = WidestLeaf(clone);
        clone.ApiEdit(leaf);
        return clone.Dependents(leaf);
    }

    private static GeneratedRepo.Project WidestLeaf(GeneratedRepo repo) =>
        repo.Libraries.Where(p => p.Layer == 0).MaxBy(p => repo.Dependents(p).Count)!;

    // shortcut: multi-targeting off, since net10.0;net11.0 needs SDK 11, which only the net11.0 host loads, and the
    // numbers are on SDK 10. Pass multiTargetEvery: 5 when the benchmark moves to -f net11.0.
    private GeneratedRepo Generate(string name) => GeneratedRepo.Generate(Path.Combine(temp, name), Count);

    // CI names the packed tool it installed, which is what users run. Without one, the build beside the tests runs.
    internal static (int ExitCode, string Output) RunMida(string root, string solution, string cache, string verb, params string[] arguments)
    {
        string[] build = [verb, solution, "--cache", cache, .. arguments];
        return Environment.GetEnvironmentVariable("MIDA_BENCH_TOOL") is { Length: > 0 } tool
            ? Start(tool, root, null, build)
            : Run(root, null, [Path.Combine(AppContext.BaseDirectory, "Mida.dll"), .. build]);
    }

    internal static string Mida(string root, string solution, string cache, string verb = "build", params string[] arguments)
    {
        var (exitCode, output) = RunMida(root, solution, cache, verb, arguments);
        if (exitCode != 0)
        {
            Assert.Fail(output);
        }

        return output;
    }

    /// <summary>
    /// Times a clean clone, which <paramref name="edit"/> changes and says which projects must then miss. With the
    /// verb test, the test projects that reference any of those must run: a test key has no early cutoff.
    /// </summary>
    private async Task Measure(bool filled, Func<GeneratedRepo, IReadOnlyCollection<GeneratedRepo.Project>> edit, string verb = "build", [CallerMemberName] string scenario = "")
    {
        string[] plainArguments = verb == "test" ? ["test", "--solution", "Repo.slnx"] : [verb, "Repo.slnx"];
        // Package downloads and dotnet's first-run setup are not what is measured, so a small repo pays for them first.
        Dotnet(GeneratedRepo.Generate(Path.Combine(temp, "prime"), 8).Root, null, "restore", "Repo.slnx");

        // What an earlier CI run left: the cache a runner restores before it builds its own clean clone at another path.
        var filledCache = Path.Combine(temp, "cache");
        // A built checkout goes as soon as it is timed: at 1024 projects a hosted runner's disk held six of them, not seven.
        if (filled)
        {
            var writer = Generate("a");
            Mida(writer.Root, "Repo.slnx", filledCache, verb);
            Directory.Delete(writer.Root, recursive: true);
        }

        // The same repo without mida, on the same machine: seconds differ between machines, the ratio to this far less.
        GeneratedRepo baseline = null!, clone = null!;
        IReadOnlyCollection<GeneratedRepo.Project> missed = [];
        var (cache, before, plainLoads) = (filledCache, Array.Empty<long>(), new List<string>());
        var runs = new List<(string Output, string Load, string Expected, string? Tests)>();
        var plainSide = new Side(
            () => (baseline, before) = (Generate("baseline"), ShutDown()),
            () => Dotnet(baseline.Root, null, plainArguments),
            _ =>
            {
                plainLoads.Add(Load(before));
                Directory.Delete(baseline.Root, recursive: true);
            });
        var midaSide = new Side(
            () =>
            {
                // Its own copy of the cache, since a run with misses adds to it.
                cache = Path.Combine(temp, $"cache{runs.Count}");
                Copy(filledCache, cache);
                clone = Generate("some/deeper/path/b");
                missed = edit(clone);
                before = ShutDown();
            },
            () => Mida(clone.Root, "Repo.slnx", cache, verb),
            output =>
            {
                var tests = clone.Projects.Count(p => p.IsTest);
                var ran = clone.Dependents(missed).Count(p => p.IsTest);
                runs.Add((output, Load(before), $"{Count - missed.Count} hits, {missed.Count} misses", verb == "test" ? TestSummary(ran, 0, tests - ran) : null));
                Directory.Delete(clone.Root, recursive: true);
            });
        var timed = Timed.Run(Iterations, Path.Combine(temp, "BenchmarkDotNet"), plainSide, filled ? null : midaSide, filled ? midaSide : null);
        var (plain, seconds, timings) = (timed.Plain, filled ? timed.Filled : timed.Empty, timed.Table);

        // A run that hits or misses the wrong projects, or runs the wrong tests, has no time worth comparing.
        foreach (var run in runs)
        {
            await Assert.That(run.Output).Contains(run.Expected);
            if (run.Tests is not null)
            {
                await Assert.That(run.Output).Contains(run.Tests);
            }
        }

        var output = runs[^1].Output;
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
        var result = SummaryLines(output);
        var table = $"""
            | scenario | projects | dotnet {verb} | mida {verb} | mida / dotnet | last run | result |
            |---|---|---|---|---|---|---|
            | {scenario} | {Count} | {plain:F1} s | {seconds:F1} s | {ratio:F2} | {change} | {result} |

            Medians of {Iterations} runs under BenchmarkDotNet, cache {megabytes} MB, {RuntimeInformation.OSDescription} {RuntimeInformation.OSArchitecture}, {Environment.ProcessorCount} processors, {cpu}

            {timings}

            | run | machine meanwhile, dotnet {verb} | machine meanwhile, mida {verb} |
            |---|---|---|
            {string.Join('\n', runs.Select((r, i) => $"| {i + 1} | {plainLoads[i]} | {r.Load} |"))}

            """;
        await File.WriteAllTextAsync(path + ".md", table);
        await File.WriteAllTextAsync(path + ".ratio", ratio.ToString("F3", CultureInfo.InvariantCulture));
        await File.WriteAllTextAsync(path + ".cpu", cpu);
        TestContext.Current!.Output.WriteLine($"{path}.md\n{table}");
    }

    /// <summary>The machine's ticks so far, for <see cref="Load"/>.</summary>
    private long[] ShutDown() => ShutDown(temp);

    internal static long[] ShutDown(string directory)
    {
        // A CI runner starts with no build servers, and a compiler server left by the run before would flatter this one.
        Run(directory, null, "build-server", "shutdown");
        return CpuTicks();
    }

    /// <summary>
    /// What the whole machine did since <paramref name="before"/>, which tells a slow disk or a crowded host from slow
    /// code: on hosted runners one commit's Fresh ratio moved by 23%. Empty off Linux.
    /// </summary>
    internal static string Load(long[] before)
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

    internal static void Copy(string source, string destination)
    {
        foreach (var file in Directory.Exists(source) ? Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories) : [])
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }
}
