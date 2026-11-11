using Microsoft.Build.Execution;
using Microsoft.Build.Framework;
using Microsoft.Build.ProjectCache;

namespace Mida;

/// <summary>
/// What MSBuild asks of the cache while it builds. Hits the walk served are answered from memory, and the projects it
/// left, which wait on something being built, are decided here once MSBuild reaches them.
/// </summary>
internal sealed class MidaCachePlugin(ProjectCache cache, string repoRoot) : ProjectCachePluginBase
{
    private readonly TargetResults targetResults = new(repoRoot);

    public override Task BeginBuildAsync(CacheContext context, PluginLoggerBase logger, CancellationToken cancellationToken) => Task.CompletedTask;

    public override Task EndBuildAsync(PluginLoggerBase logger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(logger);
        return cache.FlushAsync((warning, text) =>
        {
            if (warning)
            {
                logger.LogWarning(text);
            }
            else
            {
                // Not the logger: the console logger is quiet, and why a project always misses must not be.
                Console.WriteLine($"mida: {text}");
            }
        });
    }

    public override async Task<CacheResult> GetCacheResultAsync(BuildRequestData buildRequest, PluginLoggerBase logger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(buildRequest);
        ArgumentNullException.ThrowIfNull(logger);
        var path = buildRequest.ProjectFullPath;
        if (!cache.TryGetProject(path, TargetFramework(buildRequest.GlobalProperties.Select(p => KeyValuePair.Create(p.Name, p.EvaluatedValue))), out var project))
        {
            return CacheResult.IndicateNonCacheHit(CacheResultType.CacheNotApplicable);
        }

        if (cache.TryGetServed(project, out var served))
        {
            try
            {
                return CacheResult.IndicateCacheHit(targetResults.Replay(served.Targets, project));
            }
            catch (Exception e) when (ProjectCache.IsCacheFailure(e, cancellationToken))
            {
                logger.LogWarning($"Cache entry for {path} not used: {e.Message}");
                cache.Revoke(project, served);
            }
        }
        else if (!cache.IsAbsent(project))
        {
            var properties = buildRequest.GlobalProperties.Select(p => KeyValuePair.Create(p.Name, p.EvaluatedValue));
            if (cache.ComputeKey(project, buildRequest.TargetNames, properties, logger.LogWarning) is not { } key)
            {
                return CacheResult.IndicateNonCacheHit(CacheResultType.CacheNotApplicable);
            }

            try
            {
                if (await cache.TryRestoreAsync(project, key, cancellationToken) is { } manifest)
                {
                    var result = CacheResult.IndicateCacheHit(targetResults.Replay(manifest.Targets, project));
                    cache.Hit(project, manifest);
                    return result;
                }
            }
            catch (Exception e) when (ProjectCache.IsCacheFailure(e, cancellationToken))
            {
                logger.LogWarning($"Cache entry for {path} not used: {e.Message}");
            }
        }

        cache.Miss(project, logger.LogWarning);
        return CacheResult.IndicateNonCacheHit(CacheResultType.CacheMiss);
    }

    public override Task HandleProjectFinishedAsync(FileAccessContext fileAccessContext, BuildResult buildResult, PluginLoggerBase logger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(buildResult);
        ArgumentNullException.ThrowIfNull(logger);
        return cache.StoreAsync(
            fileAccessContext.ProjectFullPath,
            TargetFramework(fileAccessContext.GlobalProperties),
            buildResult.OverallResult == BuildResultCode.Success,
            project => targetResults.Capture(buildResult, fileAccessContext.Targets, project),
            logger.LogWarning,
            cancellationToken);
    }

    private static string TargetFramework(IEnumerable<KeyValuePair<string, string>> globalProperties) =>
        globalProperties.FirstOrDefault(p => p.Key.Equals("TargetFramework", StringComparison.OrdinalIgnoreCase)).Value ?? string.Empty;
}
