using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Execution;
using Microsoft.Build.Framework;
using Microsoft.Build.Graph;
using Microsoft.Build.Logging;
using Microsoft.Build.Prediction;
using Microsoft.Build.ProjectCache;

namespace Mida;

internal sealed class BuildRunner(BuildOptions options, string sdkVersion, string sdkPath)
{
    // Portable PDB paths, and no commit SHA in reference assemblies. See docs/notes/relocation.md
    private static readonly Dictionary<string, string> InjectedProperties = new()
    {
        ["ContinuousIntegrationBuild"] = "true",
        ["IncludeSourceRevisionInInformationalVersion"] = "false",
    };

    public int Run()
    {
        if (!Restore())
        {
            return 1;
        }

        using var collection = new ProjectCollection(InjectedProperties);
        var graph = new ProjectGraph(new ProjectGraphEntryPoint(options.Solution, InjectedProperties), collection);
        var predictions = new ProjectGraphPredictionExecutor(ProjectPredictors.AllProjectGraphPredictors, ProjectPredictors.AllProjectPredictors)
            .PredictInputsAndOutputs(graph);

        var repoRoot = RepoRoot.Find(options.Solution);
        var environment = new[]
        {
            FormattableString.Invariant($"sdk {sdkVersion}"),
            FormattableString.Invariant($"msbuild {ProjectCollection.Version}"),
            FormattableString.Invariant($"platform {RuntimeInformation.RuntimeIdentifier}"),
        };
        var dotnetRoot = Path.GetFullPath(Path.Combine(sdkPath, "..", ".."));
        var keys = new KeyComputer(repoRoot, KeyComputer.ExcludedRoots(graph, dotnetRoot), environment);
        var plugin = new MidaCachePlugin(keys, predictions, repoRoot);

        var parameters = new BuildParameters(collection)
        {
            ProjectCacheDescriptor = ProjectCacheDescriptor.FromInstance(plugin, new Dictionary<string, string>()),
            GlobalProperties = InjectedProperties,
            Loggers = Loggers(),
            MaxNodeCount = Environment.ProcessorCount,
            EnableNodeReuse = false,
        };

        var manager = BuildManager.DefaultBuildManager;
        manager.BeginBuild(parameters);
        GraphBuildResult result;
        try
        {
            result = manager.BuildRequest(new GraphBuildRequestData(graph, ["Build"]));
        }
        finally
        {
            manager.EndBuild();
        }

        Report(plugin);
        return result.OverallResult == BuildResultCode.Success ? 0 : 1;
    }

    private bool Restore()
    {
        // Evaluated apart from the build: restore rewrites the imports the build must then see.
        using var collection = new ProjectCollection();
        var properties = new Dictionary<string, string?>
        {
            ["MSBuildRestoreSessionId"] = Guid.NewGuid().ToString("D"),
            ["ExcludeRestorePackageImports"] = "true",
        };
        var request = new BuildRequestData(options.Solution, properties, null, ["Restore"], null, BuildRequestDataFlags.ClearCachesAfterBuild);
        var parameters = new BuildParameters(collection) { Loggers = Loggers(), EnableNodeReuse = false };
        return BuildManager.DefaultBuildManager.Build(parameters, request).OverallResult == BuildResultCode.Success;
    }

    private static List<ILogger> Loggers()
    {
        List<ILogger> loggers = [new ConsoleLogger(LoggerVerbosity.Quiet)];
        if (Environment.GetEnvironmentVariable("MIDA_BINLOG") is { Length: > 0 } path)
        {
            loggers.Add(new BinaryLogger { Parameters = path });
        }

        return loggers;
    }

    private void Report(MidaCachePlugin plugin)
    {
        if (options.PrintKeys)
        {
            foreach (var (project, key) in plugin.Keys.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                Console.WriteLine($"{key.Value} {project}");
            }
        }

        if (options.KeyDetailsDirectory is { } directory)
        {
            Directory.CreateDirectory(directory);
            foreach (var (project, key) in plugin.Keys)
            {
                File.WriteAllLines(Path.Combine(directory, project.Replace('/', '_') + ".key"), key.Lines);
            }
        }

        var seconds = plugin.KeyTime.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture);
        Console.WriteLine($"{plugin.Keys.Count} keys in {seconds} s, {plugin.Failures} not computable");
    }
}
