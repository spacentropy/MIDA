using Microsoft.Build.Execution;
using Microsoft.Build.ProjectCache;

namespace Mida;

/// <summary>
/// MSBuild takes its plugin when the session begins, and the real one needs the graph. Until that is set, nothing is cached.
/// </summary>
internal sealed class DeferredPlugin : ProjectCachePluginBase
{
    public MidaCachePlugin? Plugin { get; set; }

    public override Task BeginBuildAsync(CacheContext context, PluginLoggerBase logger, CancellationToken cancellationToken) => Task.CompletedTask;

    public override Task EndBuildAsync(PluginLoggerBase logger, CancellationToken cancellationToken) =>
        Plugin?.EndBuildAsync(logger, cancellationToken) ?? Task.CompletedTask;

    public override Task<CacheResult> GetCacheResultAsync(BuildRequestData buildRequest, PluginLoggerBase logger, CancellationToken cancellationToken) =>
        Plugin?.GetCacheResultAsync(buildRequest, logger, cancellationToken)
            ?? Task.FromResult(CacheResult.IndicateNonCacheHit(CacheResultType.CacheNotApplicable));

    public override Task HandleProjectFinishedAsync(FileAccessContext fileAccessContext, BuildResult buildResult, PluginLoggerBase logger, CancellationToken cancellationToken) =>
        Plugin?.HandleProjectFinishedAsync(fileAccessContext, buildResult, logger, cancellationToken) ?? Task.CompletedTask;
}
