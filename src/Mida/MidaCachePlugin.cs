using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Build.Execution;
using Microsoft.Build.Graph;
using Microsoft.Build.Prediction;
using Microsoft.Build.ProjectCache;

namespace Mida;

internal sealed class MidaCachePlugin(KeyComputer computer, ProjectGraphPredictions predictions, string repoRoot) : ProjectCachePluginBase
{
    // A project with several nodes is multi-targeted, which is out of scope: it stays a plain build.
    private readonly Dictionary<string, KeyValuePair<ProjectGraphNode, ProjectPredictions>> nodes = predictions.PredictionsPerNode
        .GroupBy(p => p.Key.ProjectInstance.FullPath, StringComparer.Ordinal)
        .Where(g => g.Count() == 1)
        .ToDictionary(g => g.Key, g => g.Single(), StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, CacheKey> keys = new(StringComparer.Ordinal);
    private long keyTicks;
    private int failures;

    public IReadOnlyDictionary<string, CacheKey> Keys => keys;

    public TimeSpan KeyTime => Stopwatch.GetElapsedTime(0, Volatile.Read(ref keyTicks));

    public int Failures => Volatile.Read(ref failures);

    public override Task BeginBuildAsync(CacheContext context, PluginLoggerBase logger, CancellationToken cancellationToken) => Task.CompletedTask;

    public override Task EndBuildAsync(PluginLoggerBase logger, CancellationToken cancellationToken) => Task.CompletedTask;

    public override Task<CacheResult> GetCacheResultAsync(BuildRequestData buildRequest, PluginLoggerBase logger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(buildRequest);
        ArgumentNullException.ThrowIfNull(logger);
        return Task.FromResult(CacheResult.IndicateNonCacheHit(ComputeKey(buildRequest, logger) ? CacheResultType.CacheMiss : CacheResultType.CacheNotApplicable));
    }

    private bool ComputeKey(BuildRequestData request, PluginLoggerBase logger)
    {
        if (!nodes.TryGetValue(request.ProjectFullPath, out var entry))
        {
            return false;
        }

        var start = Stopwatch.GetTimestamp();
        try
        {
            var name = Path.GetRelativePath(repoRoot, request.ProjectFullPath).Replace('\\', '/');
            var properties = request.GlobalProperties.Select(p => KeyValuePair.Create(p.Name, p.EvaluatedValue));
            keys[name] = computer.Compute(entry.Key, entry.Value, request.TargetNames, properties, keys);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or KeyNotFoundException or System.Text.Json.JsonException)
        {
            // A project without a key builds normally. Failing the build here would make the cache a new way to break CI.
            Interlocked.Increment(ref failures);
            logger.LogWarning($"No cache key for {request.ProjectFullPath}: {e.Message}");
            return false;
        }
        finally
        {
            Interlocked.Add(ref keyTicks, Stopwatch.GetTimestamp() - start);
        }
    }
}
