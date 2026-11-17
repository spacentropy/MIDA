using System.Diagnostics;
using System.Globalization;
using System.Text;
using Mida.Store;

namespace Mida;

/// <param name="All">Run every test project, whatever the store says of it.</param>
/// <param name="Arguments">What each test project's runner is given, which is part of a test key.</param>
internal sealed record TestRun(bool All, IReadOnlyList<string> Arguments);

/// <summary>
/// Runs the test projects of a built graph whose pass is not in the store, and stores the passes. No MSBuild in it,
/// so a run where everything hits and every test has passed before never loads MSBuild. See docs/notes/mida-test.md
/// </summary>
internal sealed class TestRunner(GraphModel graph, KeyComputer computer, IObjectStore store, TestRun run, bool readOnly, string dotnetRoot)
{
    private readonly Lock console = new();

    /// <summary>The exit code of the first test project in the graph that failed, as its runner gave it, or zero.</summary>
    public int Run()
    {
        // An outer node has no assembly: its inner nodes are the tests, one per framework.
        var tests = graph.Projects.Where(p => p.Test is not null && !p.CrossTargeting).ToList();

        // Two test nodes with one assembly both run whichever was built last, so a pass of either says nothing of its inputs.
        var shared = tests.GroupBy(t => t.TargetPath, StringComparer.Ordinal).Where(g => g.Count() > 1).SelectMany(g => g).ToHashSet();

        // All of them before the first test starts, since a test may write into the repo. Not before the build, so a
        // source edited while the build runs can pass on what was built before it: see docs/notes/mida-test.md
        // More at once than there are processors, since these wait on the store, not on the machine.
        var found = tests.AsParallel().AsOrdered().WithDegreeOfParallelism(16).Select(t => shared.Contains(t) ? (null, null) : Look(t)).ToList();
        var codes = new int[tests.Count];

        // As many at once as dotnet test runs by default. One at a time, a fresh run of 28 test projects took 3.3 times as long.
        // shortcut: the number is fixed, add an option when test projects that cannot share a machine need fewer.
        Parallel.For(0, tests.Count, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, i =>
        {
            var ((key, passed), name) = (found[i], computer.Name(tests[i]));
            if (passed is not null)
            {
                return;
            }

            var watch = Stopwatch.StartNew();
            var (code, output) = Start(tests[i]);
            codes[i] = code;
            if (code == 0 && key is not null && !readOnly)
            {
                Store(name, key, watch.Elapsed);
            }

            // Whole, so the lines of two test projects do not mix. Only a failure's output is worth its length.
            lock (console)
            {
                Console.Write(code == 0 ? string.Empty : output);

                // Not zero also for a run that found no test, which Microsoft.Testing.Platform ends with 8: nothing passed there.
                Console.WriteLine(code == 0 ? $"mida: {name} passed" : $"mida: {name} failed with exit code {code}");
            }
        });

        var passedBefore = found.Count(f => f.Passed is not null);
        var seconds = TimeSpan.FromTicks(found.Sum(f => f.Passed?.Ticks ?? 0)).TotalSeconds.ToString("F1", CultureInfo.InvariantCulture);
        Console.WriteLine($"{tests.Count - passedBefore} test projects run, {codes.Count(c => c != 0)} failed, {passedBefore} passed before, {seconds} s of test time saved");
        return codes.FirstOrDefault(c => c != 0);
    }

    private static string ObjectKey(CacheKey key) => $"v1/test/{key.Value}";

    /// <summary>The test key, and how long the stored pass took. Neither for a VSTest project, which is refused when it starts.</summary>
    private (CacheKey? Key, TimeSpan? Passed) Look(ProjectModel test) =>
        test.Test == "mtp" && Key(test) is { } key
            ? (key, run.All ? null : PassedBefore(computer.Name(test), key))
            : (null, null);

    // Any failure of a key or of the store runs the test: the cache must never be why a test did not run.
    private CacheKey? Key(ProjectModel test)
    {
        try
        {
            return computer.ComputeTest(test, run.Arguments);
        }
        catch (Exception e) when (ProjectCache.IsCacheFailure(e, CancellationToken.None))
        {
            Console.WriteLine($"warning: No test key for {test.Path}: {e.Message}");
            return null;
        }
    }

    /// <summary>How long the stored pass took, or null without one.</summary>
    private TimeSpan? PassedBefore(string name, CacheKey key)
    {
        try
        {
            return store.GetAsync(ObjectKey(key), CancellationToken.None).GetAwaiter().GetResult() is { } record
                && long.TryParse(Encoding.UTF8.GetString(record), CultureInfo.InvariantCulture, out var ticks)
                    ? TimeSpan.FromTicks(ticks)
                    : null;
        }
        catch (Exception e) when (ProjectCache.IsCacheFailure(e, CancellationToken.None))
        {
            Console.WriteLine($"warning: Test result of {name} not read: {e.Message}");
            return null;
        }
    }

    private void Store(string name, CacheKey key, TimeSpan duration)
    {
        try
        {
            var record = Encoding.UTF8.GetBytes(duration.Ticks.ToString(CultureInfo.InvariantCulture));
            store.TryCreateAsync(ObjectKey(key), record, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception e) when (ProjectCache.IsCacheFailure(e, CancellationToken.None))
        {
            Console.WriteLine($"warning: Test result of {name} not stored: {e.Message}");
        }
    }

    /// <summary>
    /// Runs the built test project the way dotnet test does after its own evaluation, in this directory, and returns
    /// what it wrote. Never zero for a test project that did not run, which would read as a pass.
    /// </summary>
    private (int ExitCode, string Output) Start(ProjectModel test)
    {
        // Failed, not skipped: a skipped test project would read as covered.
        if (test.Test != "mtp")
        {
            return (1, $"mida: {computer.Name(test)} runs on VSTest, and mida test runs Microsoft.Testing.Platform projects only{Environment.NewLine}");
        }

        if (test.TargetPath is not { } assembly || !File.Exists(assembly))
        {
            return (1, $"mida: {computer.Name(test)} has no built assembly to test{Environment.NewLine}");
        }

        // The SDK's own dotnet, so a test application needs no DOTNET_ROOT to find its runtime.
        // shortcut: the project's RunCommand, RunArguments and .runsettings are not read, and a self-contained test
        // application has nothing for dotnet exec. Store RunCommand in the graph when a repo needs either.
        return Program.Dotnet(dotnetRoot, null, ["exec", assembly, .. run.Arguments]);
    }
}
