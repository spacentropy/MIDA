using System.Runtime.ExceptionServices;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Filters;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.InProcess.NoEmit;

namespace Mida.Tests;

/// <summary>One way to build: Before makes a clean checkout and After removes it, neither of them timed.</summary>
internal sealed record Side(Action Before, Func<string> Run, Action<string> After);

/// <summary>
/// Plain dotnet, the baseline, beside mida with an empty cache, with a filled one, or both, under BenchmarkDotNet in
/// this process: the work is in child processes, so a Debug test assembly times the same. Each iteration is one run,
/// and every iteration of one side runs before the next side's, in the order above.
/// </summary>
// shortcut: BenchmarkDotNet does not alternate the sides as the loop before it did, so load that drifts during a run
// biases the ratio. Medians soften that; alternate by hand when a run looks drifted.
#pragma warning disable CA1822 // BenchmarkDotNet needs instance methods.
public class Timed
{
    private static Side plain = null!;
    private static Side? empty;
    private static Side? filled;

    // BenchmarkDotNet loses a benchmark's exception in a NullReferenceException of its own, so the first one waits here.
    private static Exception? failure;

    private string output = string.Empty;

    [IterationSetup(Target = nameof(Dotnet))]
    public void BeforeDotnet() => Guard(plain.Before);

    [Benchmark(Baseline = true)]
    public void Dotnet() => Guard(() => output = plain.Run());

    [IterationCleanup(Target = nameof(Dotnet))]
    public void AfterDotnet() => Guard(() => plain.After(output));

    [IterationSetup(Target = nameof(MidaEmptyCache))]
    public void BeforeEmpty() => Guard(empty!.Before);

    [Benchmark]
    public void MidaEmptyCache() => Guard(() => output = empty!.Run());

    [IterationCleanup(Target = nameof(MidaEmptyCache))]
    public void AfterEmpty() => Guard(() => empty!.After(output));

    [IterationSetup(Target = nameof(MidaFilledCache))]
    public void BeforeFilled() => Guard(filled!.Before);

    [Benchmark]
    public void MidaFilledCache() => Guard(() => output = filled!.Run());

    [IterationCleanup(Target = nameof(MidaFilledCache))]
    public void AfterFilled() => Guard(() => filled!.After(output));

    /// <summary>
    /// Runs each side given <paramref name="iterations"/> times and returns the medians in seconds, NaN for a side not
    /// given, and BenchmarkDotNet's table.
    /// </summary>
    internal static (double Plain, double Empty, double Filled, string Table) Run(int iterations, string artifacts, Side plainSide, Side? emptyCache = null, Side? filledCache = null)
    {
        (plain, empty, filled, failure) = (plainSide, emptyCache, filledCache, null);

        // ColdStart has no jitting run, which would be one more whole build per side.
        var job = new Job("runs")
            .WithStrategy(RunStrategy.ColdStart)
            .WithLaunchCount(1)
            .WithWarmupCount(0)
            .WithIterationCount(iterations)
            .WithInvocationCount(1)
            .WithUnrollFactor(1)
            .WithToolchain(new InProcessNoEmitToolchain(TimeSpan.FromHours(6), logOutput: true));
        var config = ManualConfig.CreateMinimumViable()
            .AddJob(job)
            .AddLogger(ConsoleLogger.Default)
            .AddFilter(new SimpleFilter(b => b.Descriptor.WorkloadMethod.Name switch
            {
                nameof(MidaEmptyCache) => emptyCache is not null,
                nameof(MidaFilledCache) => filledCache is not null,
                _ => true,
            }))
            .WithOptions(ConfigOptions.DisableOptimizationsValidator)
            .WithArtifactsPath(artifacts);
        var summary = BenchmarkRunner.Run<Timed>(config);

        // Set by the benchmarks, which the analyzer does not see.
        if (Volatile.Read(ref failure) is { } thrown)
        {
            ExceptionDispatchInfo.Throw(thrown);
        }

        // BenchmarkDotNet reports a failed run as NA and carries on.
        var reports = summary.Reports.ToDictionary(r => r.BenchmarkCase.Descriptor.WorkloadMethod.Name);
        if (summary.HasCriticalValidationErrors || reports.Count == 0 || reports.Values.Any(r => !r.Success || r.ResultStatistics is null))
        {
            throw new InvalidOperationException($"BenchmarkDotNet failed: {string.Join("; ", summary.ValidationErrors.Select(e => e.Message))}");
        }

        var logger = new AccumulationLogger();
        MarkdownExporter.GitHub.ExportToLog(summary, logger);
        return (Seconds(nameof(Dotnet)), Seconds(nameof(MidaEmptyCache)), Seconds(nameof(MidaFilledCache)), logger.GetLog());

        double Seconds(string name) => reports.TryGetValue(name, out var report) ? report.ResultStatistics!.Median / 1e9 : double.NaN;
    }

    // After a failure the rest is skipped: its checkout may not exist.
#pragma warning disable CA1031 // Rethrown by Run.
    private static void Guard(Action action)
    {
        if (failure is not null)
        {
            return;
        }

        try
        {
            action();
        }
        catch (Exception e)
        {
            failure = e;
        }
    }
#pragma warning restore CA1031
}
#pragma warning restore CA1822
