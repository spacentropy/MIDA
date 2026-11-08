using System.Globalization;
using System.Runtime.InteropServices;
using Google.Apis.Auth.OAuth2;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Execution;
using Microsoft.Build.Framework;
using Microsoft.Build.Graph;
using Microsoft.Build.Logging;
using Microsoft.Build.Prediction;
using Microsoft.Build.ProjectCache;
using Mida.Store;

namespace Mida;

internal sealed class BuildRunner(BuildOptions options, string sdkVersion, string sdkPath)
{
    // Portable PDB paths, and no commit SHA in reference assemblies. See docs/notes/relocation.md
    private static readonly Dictionary<string, string> InjectedProperties = new()
    {
        ["ContinuousIntegrationBuild"] = "true",
        ["IncludeSourceRevisionInInformationalVersion"] = "false",
    };

    private static readonly HttpClient Http = new();

    // The user's -p wins over an injected one: global properties are in the key, so that costs hits, never correctness.
    private readonly Dictionary<string, string> globalProperties = InjectedProperties
        .Where(p => !options.Properties.ContainsKey(p.Key))
        .Concat(options.Properties)
        .ToDictionary(StringComparer.OrdinalIgnoreCase);

    public int Run()
    {
        if (!Restore())
        {
            return 1;
        }

        using var collection = new ProjectCollection(globalProperties);
        var graph = new ProjectGraph(new ProjectGraphEntryPoint(options.Solution, globalProperties), collection);
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
        var plugin = new MidaCachePlugin(keys, predictions, new OutputCache(Store(), repoRoot), repoRoot, options.ReadOnly);

        var parameters = new BuildParameters(collection)
        {
            ProjectCacheDescriptor = ProjectCacheDescriptor.FromInstance(plugin, new Dictionary<string, string>()),
            GlobalProperties = globalProperties,
            Loggers = Loggers(),
            MaxNodeCount = Environment.ProcessorCount,
            EnableNodeReuse = false,
            // Tasks in this process bind to mida's own copies, such as System.CodeDom 7.0 from Google.Apis.Auth, and fail.
            DisableInProcNode = true,
        };

        var manager = BuildManager.DefaultBuildManager;
        manager.BeginBuild(parameters);
        GraphBuildResult result;
        // Deleted after the nodes exit: it is their working directory, and evaluation fails once it is gone.
        var nodesDirectory = Directory.CreateTempSubdirectory("mida-nodes-");
        try
        {
            StartNodes(manager, parameters.MaxNodeCount, nodesDirectory.FullName);
            result = manager.BuildRequest(new GraphBuildRequestData(graph, ["Build"]));
        }
        finally
        {
            manager.EndBuild();
            nodesDirectory.Delete(recursive: true);
        }

        Report(plugin);
        return result.OverallResult == BuildResultCode.Success ? 0 : 1;
    }

    private IObjectStore Store()
    {
        var local = new FileSystemStore(options.CacheDirectory);
        if (options.Bucket is not { } bucket)
        {
            return local;
        }

        // The variable Google's client libraries read for an emulator, which needs no credentials.
        if (Environment.GetEnvironmentVariable("STORAGE_EMULATOR_HOST") is { Length: > 0 } emulator)
        {
            var endpoint = new Uri(emulator.Contains("://", StringComparison.Ordinal) ? emulator : "http://" + emulator);
            return new TieredStore(local, new GcsStore(Http, endpoint, bucket, options.BucketPrefix, _ => Task.FromResult<string?>(null)));
        }

        // Application Default Credentials: GOOGLE_APPLICATION_CREDENTIALS, gcloud's, or the metadata server. Found on first use,
        // so a run without them warns per project and builds.
        var credential = new Lazy<Task<GoogleCredential>>(async () =>
            (await GoogleCredential.GetApplicationDefaultAsync()).CreateScoped("https://www.googleapis.com/auth/devstorage.read_write"));
        return new TieredStore(local, new GcsStore(Http, new Uri("https://storage.googleapis.com/"), bucket, options.BucketPrefix, async token =>
            await ((ITokenAccess)await credential.Value).GetAccessTokenForRequestAsync(cancellationToken: token)));
    }

    /// <summary>
    /// MSBuild starts worker nodes holding a lock, and on Unix their pipe handshake needs a pool thread. Started during
    /// a burst of hits, every pool thread waits on that lock, so the first misses after many hits stalled for up to a
    /// minute. Started here, before any hit, the nodes stay up for the graph build. See docs/notes/benchmarks.md
    /// </summary>
    private static void StartNodes(BuildManager manager, int count, string directory)
    {
        var project = Path.Combine(directory, "nodes.proj");
        // Distinct global properties make distinct requests, which the scheduler spreads over new nodes.
        var requests = string.Concat(Enumerable.Range(0, count).Select(i => $"""<Node Include="{project}" AdditionalProperties="Node={i}" />"""));
        File.WriteAllText(project, $"""
            <Project>
              <ItemGroup>{requests}</ItemGroup>
              <Target Name="Start"><MSBuild Projects="@(Node)" Targets="Started" BuildInParallel="true" /></Target>
              <Target Name="Started" />
            </Project>
            """);
        manager.BuildRequest(new BuildRequestData(project, new Dictionary<string, string?>(), null, ["Start"], null));
    }

    private bool Restore()
    {
        // Evaluated apart from the build: restore rewrites the imports the build must then see.
        using var collection = new ProjectCollection();
        // The same properties as the build: a repo may condition its restore on ContinuousIntegrationBuild.
        var properties = new Dictionary<string, string?>(globalProperties!)
        {
            ["MSBuildRestoreSessionId"] = Guid.NewGuid().ToString("D"),
            ["ExcludeRestorePackageImports"] = "true",
        };
        var request = new BuildRequestData(options.Solution, properties, null, ["Restore"], null, BuildRequestDataFlags.ClearCachesAfterBuild);
        var parameters = new BuildParameters(collection)
        {
            Loggers = Loggers(), MaxNodeCount = Environment.ProcessorCount, EnableNodeReuse = false, DisableInProcNode = true,
        };
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
            foreach (var (project, key) in plugin.Keys)
            {
                // Mirrors the repo layout: flattening the path would let two projects share one file.
                var file = Path.Combine(directory, project + ".key");
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllLines(file, key.Lines);
            }
        }

        var seconds = plugin.KeyTime.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture);
        Console.WriteLine($"{plugin.Keys.Count} keys in {seconds} s, {plugin.Failures} not computable");
        var saved = plugin.TimeSaved.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture);
        Console.WriteLine($"{plugin.Hits} hits, {plugin.Misses} misses, {saved} s of project build time saved");
    }
}
