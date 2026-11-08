using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Build.Execution;
using Microsoft.Build.Framework;
using Microsoft.Build.Graph;
using Microsoft.Build.Prediction;
using Microsoft.Build.ProjectCache;
using Mida.Store;

namespace Mida;

internal sealed class MidaCachePlugin(KeyComputer computer, ProjectGraphPredictions predictions, OutputCache cache, string repoRoot, bool readOnly)
    : ProjectCachePluginBase
{
    private static readonly Manifest NoOutputs = new([], [], []);

    private static readonly string[] OwnedRootProperties = ["BaseOutputPath", "BaseIntermediateOutputPath"];

    // A project with several nodes is multi-targeted, which is out of scope: it stays a plain build.
    private readonly Dictionary<string, KeyValuePair<ProjectGraphNode, ProjectPredictions>> nodes = predictions.PredictionsPerNode
        .GroupBy(p => p.Key.ProjectInstance.FullPath, StringComparer.Ordinal)
        .Where(g => g.Count() == 1)
        .ToDictionary(g => g.Key, g => g.Single(), StringComparer.Ordinal);

    private readonly HashSet<ProjectGraphNode> sharing = SharingDirectories(predictions);
    private readonly ConcurrentDictionary<string, CacheKey> keys = new(StringComparer.Ordinal);
    private readonly TargetResults targetResults = new(repoRoot);
    private readonly ConcurrentQueue<Task> saves = new();
    private readonly ConcurrentQueue<Action<PluginLoggerBase>> saveLogs = new();
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

    public override async Task EndBuildAsync(PluginLoggerBase logger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(logger);
        await Task.WhenAll(saves);
        foreach (var log in saveLogs)
        {
            log(logger);
        }
    }

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
                var result = CacheResult.IndicateCacheHit(targetResults.Replay(manifest.Targets, entry.Key.ProjectInstance));
                RemoveObsolete(entry, manifest);
                Interlocked.Increment(ref hits);
                return result;
            }
        }
        catch (Exception e) when (IsCacheFailure(e, cancellationToken))
        {
            logger.LogWarning($"Cache entry for {buildRequest.ProjectFullPath} not used: {e.Message}");
        }

        try
        {
            // MSBuild's clean reads a file list the cache leaves out, so what a hit restored would be stored under this key.
            RemoveObsolete(entry, NoOutputs);
        }
        catch (Exception e) when (IsCacheFailure(e, cancellationToken))
        {
            logger.LogWarning($"Outputs of {buildRequest.ProjectFullPath} not cleared: {e.Message}");
        }

        Interlocked.Increment(ref misses);
        return CacheResult.IndicateNonCacheHit(CacheResultType.CacheMiss);
    }

    public override Task HandleProjectFinishedAsync(FileAccessContext fileAccessContext, BuildResult buildResult, PluginLoggerBase logger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(buildResult);
        ArgumentNullException.ThrowIfNull(logger);
        var path = fileAccessContext.ProjectFullPath;
        if (readOnly
            || buildResult.OverallResult != BuildResultCode.Success
            || !nodes.TryGetValue(path, out var entry)
            || !keys.TryGetValue(Name(path), out var key))
        {
            return Task.CompletedTask;
        }

        List<ManifestTarget> targets;
        try
        {
            // Now, not in the background: MSBuild merges later target results into this BuildResult.
            targets = targetResults.Capture(buildResult, fileAccessContext.Targets, entry.Key.ProjectInstance);
        }
        catch (Exception e) when (IsCacheFailure(e, cancellationToken))
        {
            logger.LogWarning($"Outputs of {path} not stored: {e.Message}");
            return Task.CompletedTask;
        }

        // A dependent may already write to a shared directory, so the scan must happen before this hook returns.
        if (sharing.Contains(entry.Key))
        {
            return SaveAsync(entry, key, targets, path, cancellationToken);
        }

        // MSBuild waits for this hook holding its BuildManager lock, and the pool threads a save needs queue up
        // behind that lock, which stalled misses for up to a minute. See docs/notes/benchmarks.md
        // No token: MSBuild cancels it when the build ends, which is before the last saves do.
        saves.Enqueue(Task.Run(() => SaveAsync(entry, key, targets, path, CancellationToken.None), CancellationToken.None));
        return Task.CompletedTask;
    }

    private async Task SaveAsync(KeyValuePair<ProjectGraphNode, ProjectPredictions> entry, CacheKey key, List<ManifestTarget> targets, string path, CancellationToken cancellationToken)
    {
        try
        {
            var outputs = ProjectOutputs.Scan(entry.Key.ProjectInstance, entry.Value);
            if (ProjectOutputs.HoldCheckoutPaths(outputs, repoRoot))
            {
                saveLogs.Enqueue(logger => logger.LogMessage($"Outputs of {path} not stored: static web assets name this checkout", MessageImportance.High));
                return;
            }

            var copies = computer.DependencyCopies(entry.Key, outputs);
            var stored = await cache.SaveAsync(
                key.Value,
                outputs.Where(f => !copies.ContainsKey(f)),
                // In a shared directory a dependency's file is its own source, and a restore would delete it before copying.
                [.. copies.Where(c => c.Key != c.Value).Select(c => new ManifestReference(Name(c.Key), Name(c.Value))).OrderBy(r => r.Path, StringComparer.Ordinal)],
                targets,
                cancellationToken);
            if (!stored)
            {
                saveLogs.Enqueue(logger => logger.LogWarning($"Outputs of {path} not stored: another build stored different outputs for this key, so the build is not deterministic"));
            }
        }
        catch (Exception e) when (IsCacheFailure(e, cancellationToken))
        {
            saveLogs.Enqueue(logger => logger.LogWarning($"Outputs of {path} not stored: {e.Message}"));
        }
    }

    // Any failure is a miss: failing the build would make the cache a new way to break CI. An allow-list let a store's
    // NotSupportedException or its own timeout fail the build with no message. Only the build's own cancellation passes.
    private static bool IsCacheFailure(Exception e, CancellationToken build) =>
        !(e is OperationCanceledException && build.IsCancellationRequested);

    private string Name(string path) => computer.Relative(path);

    // A scan sees everything under a project's directories, so another project's outputs there are not obsolete.
    private static HashSet<ProjectGraphNode> SharingDirectories(ProjectGraphPredictions predictions)
    {
        var directories = predictions.PredictionsPerNode
            .Select(p => (Node: p.Key, Directories: ProjectOutputs.Directories(p.Key.ProjectInstance, p.Value).ToList()))
            .ToList();
        return
        [
            .. directories.Where(own => directories.Any(other => other.Node != own.Node
                && other.Directories.Any(o => own.Directories.Any(d => o.StartsWith(d, KeyComputer.PathComparison)))))
                .Select(own => own.Node),
        ];
    }

    private void RemoveObsolete(KeyValuePair<ProjectGraphNode, ProjectPredictions> entry, Manifest manifest)
    {
        var (node, own) = entry;
        if (!sharing.Contains(node))
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
        catch (Exception e) when (IsCacheFailure(e, CancellationToken.None))
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
