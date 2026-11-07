using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Build.Execution;
using Microsoft.Build.Framework;
using Microsoft.Build.Graph;
using Microsoft.Build.Prediction;
using Microsoft.Build.ProjectCache;
using Mida.Store;

namespace Mida;

internal sealed class MidaCachePlugin(KeyComputer computer, ProjectGraphPredictions predictions, OutputCache cache, string repoRoot) : ProjectCachePluginBase
{
    private static readonly Manifest NoOutputs = new([], [], []);

    private static readonly string[] OwnedRootProperties = ["BaseOutputPath", "BaseIntermediateOutputPath"];

    // A project with several nodes is multi-targeted, which is out of scope: it stays a plain build.
    private readonly Dictionary<string, KeyValuePair<ProjectGraphNode, ProjectPredictions>> nodes = predictions.PredictionsPerNode
        .GroupBy(p => p.Key.ProjectInstance.FullPath, StringComparer.Ordinal)
        .Where(g => g.Count() == 1)
        .ToDictionary(g => g.Key, g => g.Single(), StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, CacheKey> keys = new(StringComparer.Ordinal);
    private readonly TargetResults targetResults = new(repoRoot);
    private long keyTicks;
    private int failures;
    private int hits;
    private int misses;

    public IReadOnlyDictionary<string, CacheKey> Keys => keys;

    public TimeSpan KeyTime => Stopwatch.GetElapsedTime(0, Volatile.Read(ref keyTicks));

    public int Failures => Volatile.Read(ref failures);

    public int Hits => Volatile.Read(ref hits);

    public int Misses => Volatile.Read(ref misses);

    public override Task BeginBuildAsync(CacheContext context, PluginLoggerBase logger, CancellationToken cancellationToken) => Task.CompletedTask;

    public override Task EndBuildAsync(PluginLoggerBase logger, CancellationToken cancellationToken) => Task.CompletedTask;

    public override async Task<CacheResult> GetCacheResultAsync(BuildRequestData buildRequest, PluginLoggerBase logger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(buildRequest);
        ArgumentNullException.ThrowIfNull(logger);
        if (ComputeKey(buildRequest, logger) is not { } key)
        {
            return CacheResult.IndicateNonCacheHit(CacheResultType.CacheNotApplicable);
        }

        var entry = nodes[buildRequest.ProjectFullPath];
        try
        {
            if (await cache.TryRestoreAsync(key.Value, cancellationToken) is { } manifest)
            {
                var result = CacheResult.IndicateCacheHit(targetResults.Replay(manifest.Targets));
                RemoveObsolete(entry, manifest);
                Interlocked.Increment(ref hits);
                return result;
            }
        }
        catch (Exception e) when (IsCacheFailure(e))
        {
            logger.LogWarning($"Cache entry for {buildRequest.ProjectFullPath} not used: {e.Message}");
        }

        try
        {
            // MSBuild's clean reads a file list the cache leaves out, so what a hit restored would be stored under this key.
            RemoveObsolete(entry, NoOutputs);
        }
        catch (Exception e) when (IsCacheFailure(e))
        {
            logger.LogWarning($"Outputs of {buildRequest.ProjectFullPath} not cleared: {e.Message}");
        }

        Interlocked.Increment(ref misses);
        return CacheResult.IndicateNonCacheHit(CacheResultType.CacheMiss);
    }

    public override async Task HandleProjectFinishedAsync(FileAccessContext fileAccessContext, BuildResult buildResult, PluginLoggerBase logger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(buildResult);
        ArgumentNullException.ThrowIfNull(logger);
        var path = fileAccessContext.ProjectFullPath;
        if (buildResult.OverallResult != BuildResultCode.Success
            || !nodes.TryGetValue(path, out var entry)
            || !keys.TryGetValue(Name(path), out var key))
        {
            return;
        }

        try
        {
            var outputs = ProjectOutputs.Scan(entry.Key.ProjectInstance, entry.Value);
            if (ProjectOutputs.HoldCheckoutPaths(outputs, repoRoot))
            {
                logger.LogMessage($"Outputs of {path} not stored: static web assets name this checkout", MessageImportance.High);
                return;
            }

            var copies = computer.DependencyCopies(entry.Key, outputs);
            await cache.SaveAsync(
                key.Value,
                outputs.Where(f => !copies.ContainsKey(f)),
                // In a shared directory a dependency's file is its own source, and a restore would delete it before copying.
                [.. copies.Where(c => c.Key != c.Value).Select(c => new ManifestReference(Name(c.Key), Name(c.Value))).OrderBy(r => r.Path, StringComparer.Ordinal)],
                targetResults.Capture(buildResult, fileAccessContext.Targets),
                cancellationToken);
        }
        catch (Exception e) when (IsCacheFailure(e))
        {
            logger.LogWarning($"Outputs of {path} not stored: {e.Message}");
        }
    }

    // A failure here is a miss: failing the build would make the cache a new way to break CI.
    private static bool IsCacheFailure(Exception e) =>
        e is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException
            or KeyNotFoundException or ArgumentException or System.Text.Json.JsonException;

    private string Name(string path) => Path.GetRelativePath(repoRoot, path).Replace('\\', '/');

    // A scan sees everything under a project's directories, so another project's outputs there are not obsolete.
    private void RemoveObsolete(KeyValuePair<ProjectGraphNode, ProjectPredictions> entry, Manifest manifest)
    {
        var (node, own) = entry;
        var directories = ProjectOutputs.Directories(node.ProjectInstance, own).ToList();
        var shared = predictions.PredictionsPerNode
            .Where(other => other.Key != node)
            .SelectMany(other => ProjectOutputs.Directories(other.Key.ProjectInstance, other.Value))
            .Any(other => directories.Any(d => other.StartsWith(d, KeyComputer.PathComparison)));
        if (!shared)
        {
            // Only bin/ and obj/ belong to the build: an OutDir elsewhere can hold the repo's own files.
            var project = node.ProjectInstance;
            var owned = OwnedRootProperties
                .Select(p => KeyComputer.AbsolutePath(project, project.GetPropertyValue(p)))
                .OfType<string>()
                .Select(KeyComputer.WithSeparator)
                .ToList();
            cache.RemoveObsolete(manifest, ProjectOutputs.Scan(project, own).Where(f => owned.Any(o => f.StartsWith(o, KeyComputer.PathComparison))));
        }
    }

    private CacheKey? ComputeKey(BuildRequestData request, PluginLoggerBase logger)
    {
        if (!nodes.TryGetValue(request.ProjectFullPath, out var entry))
        {
            return null;
        }

        var start = Stopwatch.GetTimestamp();
        try
        {
            var properties = request.GlobalProperties.Select(p => KeyValuePair.Create(p.Name, p.EvaluatedValue));
            return keys[Name(request.ProjectFullPath)] = computer.Compute(entry.Key, entry.Value, request.TargetNames, properties, keys);
        }
        catch (Exception e) when (IsCacheFailure(e))
        {
            // A project without a key builds normally.
            Interlocked.Increment(ref failures);
            logger.LogWarning($"No cache key for {request.ProjectFullPath}: {e.Message}");
            return null;
        }
        finally
        {
            Interlocked.Add(ref keyTicks, Stopwatch.GetTimestamp() - start);
        }
    }
}
