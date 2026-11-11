using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Build.Definition;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Evaluation.Context;
using Microsoft.Build.Execution;
using Microsoft.Build.Framework;
using Microsoft.Build.Graph;
using Microsoft.Build.Logging;
using Microsoft.Build.ProjectCache;

namespace Mida;

/// <summary>What a build needs MSBuild for. A run that takes its graph from the store and hits every project asks for none of it.</summary>
internal interface IBuildHost : IDisposable
{
    bool Restore();

    /// <summary>Evaluates the graph and writes down what that depended on. Run after <see cref="Restore"/>.</summary>
    Evaluated Evaluate();

    /// <summary>Builds what <see cref="ProjectCache.WalkAsync"/> left. False when the build failed.</summary>
    bool Build(ProjectCache cache);
}

/// <summary>
/// Hosts MSBuild in this process. Nothing outside this file, <see cref="GraphEvaluation"/> and the plugin names an
/// MSBuild type, so a run that needs none of them does not load MSBuild.
/// </summary>
internal sealed class MSBuildHost : IBuildHost
{
    private readonly BuildOptions options;
    private readonly Dictionary<string, string> globalProperties;
    private readonly string sdkVersion;
    private readonly Checkout checkout;
    private readonly DeferredPlugin deferred = new();
    private ProjectCollection? collection;
    private ProjectGraph? graph;
    private Task? nodes;
    private DirectoryInfo? nodesDirectory;

    private MSBuildHost(BuildOptions options, Dictionary<string, string> globalProperties, string sdkVersion, Checkout checkout)
    {
        this.options = options;
        this.globalProperties = globalProperties;
        this.sdkVersion = sdkVersion;
        this.checkout = checkout;
    }

    // Not inlined: the caller must not name an MSBuild type, or the JIT would load MSBuild for a run that needs none.
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static IBuildHost Create(BuildOptions options, Dictionary<string, string> globalProperties, string sdkVersion, Checkout checkout) =>
        new MSBuildHost(options, globalProperties, sdkVersion, checkout);

    public bool Restore()
    {
        // Evaluated apart from the build: restore rewrites the imports the build must then see.
        using var restoring = new ProjectCollection();
        // The same properties as the build: a repo may condition its restore on ContinuousIntegrationBuild.
        var properties = new Dictionary<string, string?>(globalProperties!, StringComparer.OrdinalIgnoreCase)
        {
            ["MSBuildRestoreSessionId"] = Guid.NewGuid().ToString("D"),
            ["ExcludeRestorePackageImports"] = "true",
        };

        // NuGet then reads the projects in one process of its own, where the default has every worker node evaluate them:
        // restore of 128 projects took 2.2 s, not 2.9 s. NuGet calls it experimental, so -p:RestoreUseStaticGraphEvaluation=false turns it off.
        properties.TryAdd("RestoreUseStaticGraphEvaluation", "true");
        var request = new BuildRequestData(options.Solution, properties, null, ["Restore"], null, BuildRequestDataFlags.ClearCachesAfterBuild);
        var parameters = new BuildParameters(restoring)
        {
            Loggers = Loggers(), MaxNodeCount = Environment.ProcessorCount, EnableNodeReuse = false, DisableInProcNode = true,
        };
        return Manager.Build(parameters, request).OverallResult == BuildResultCode.Success;
    }

    public Evaluated Evaluate()
    {
        var questions = new Questions(checkout.DotnetRoot, checkout.Hashes, checkout.Variables);
        Begin(new ProjectCollection(globalProperties, [new EnvironmentLogger(questions)], ToolsetDefinitionLocations.Default));

        // The default factory with one change: the evaluation asks the disk through a file system that writes the questions down.
        var context = EvaluationContext.Create(EvaluationContext.SharingPolicy.Shared, new RecordingFileSystem(questions));
        graph = new ProjectGraph(
            [new ProjectGraphEntryPoint(options.Solution, globalProperties)],
            collection,
            (path, properties, projects) => ProjectInstance.FromFile(
                path,
                new ProjectOptions { GlobalProperties = properties, ProjectCollection = projects, EvaluationContext = context }));
        Phase.Mark("graph evaluation");
        string[] environment =
        [
            FormattableString.Invariant($"sdk {sdkVersion}"),
            FormattableString.Invariant($"msbuild {ProjectCollection.Version}"),
            FormattableString.Invariant($"platform {RuntimeInformation.RuntimeIdentifier}"),
        ];
        var evaluated = GraphEvaluation.Read(graph, questions, checkout.Tokens, options.Solution, checkout.DotnetRoot, environment);
        Phase.Mark("prediction and questions");
        return evaluated;
    }

    public bool Build(ProjectCache cache)
    {
        if (graph is null)
        {
            // The graph came from the store, and MSBuild still has to evaluate what it builds.
            Begin(new ProjectCollection(globalProperties));
            graph = new ProjectGraph(new ProjectGraphEntryPoint(options.Solution, globalProperties), collection);
            Phase.Mark("graph evaluation");
        }

        deferred.Plugin = new MidaCachePlugin(cache, checkout.RepoRoot);
        nodes!.GetAwaiter().GetResult();
        return Manager.BuildRequest(new GraphBuildRequestData(graph, ["Build"])).OverallResult == BuildResultCode.Success;
    }

    public void Dispose()
    {
        if (nodesDirectory is not null)
        {
            try
            {
                // A build that ends while the nodes still start would end under them.
                nodes!.GetAwaiter().GetResult();
            }
            finally
            {
                Manager.EndBuild();
                // Deleted after the nodes exit: it is their working directory, and evaluation fails once it is gone.
                nodesDirectory.Delete(recursive: true);
            }
        }

        collection?.Dispose();
    }

    /// <summary>
    /// Begins the build session and starts the worker nodes, which goes on beside the evaluation that follows. Started
    /// after it, the nodes cost a build of 128 projects 0.3 s more.
    /// </summary>
    private void Begin(ProjectCollection projects)
    {
        collection = projects;
        var parameters = new BuildParameters(projects)
        {
            // MSBuild takes its plugin when the session begins, and the real one needs the graph.
            ProjectCacheDescriptor = ProjectCacheDescriptor.FromInstance(deferred, new Dictionary<string, string>()),
            GlobalProperties = globalProperties,
            Loggers = Loggers(),
            MaxNodeCount = Environment.ProcessorCount,
            EnableNodeReuse = false,
            // Tasks in this process bind to mida's own copies, such as System.CodeDom 7.0 from Google.Apis.Auth, and fail.
            DisableInProcNode = true,
        };
        Manager.BeginBuild(parameters);
        nodesDirectory = Directory.CreateTempSubdirectory("mida-nodes-");
        var directory = nodesDirectory.FullName;
        nodes = Task.Run(() => StartNodes(parameters.MaxNodeCount, directory));
    }

    /// <summary>
    /// MSBuild starts worker nodes holding a lock, and on Unix their pipe handshake needs a pool thread. Started during
    /// a burst of hits, every pool thread waits on that lock, so the first misses after many hits stalled for up to a
    /// minute. Started here, before any hit, the nodes stay up for the graph build. See docs/notes/benchmarks.md
    /// </summary>
    private static void StartNodes(int count, string directory)
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
        Manager.BuildRequest(new BuildRequestData(project, new Dictionary<string, string?>(), null, ["Start"], null));
    }

    // The process's own, which this host does not own and so does not dispose.
    private static BuildManager Manager => BuildManager.DefaultBuildManager;

    private static List<ILogger> Loggers()
    {
        List<ILogger> loggers = [new ConsoleLogger(LoggerVerbosity.Quiet)];
        if (Environment.GetEnvironmentVariable("MIDA_BINLOG") is { Length: > 0 } path)
        {
            loggers.Add(new BinaryLogger { Parameters = path });
        }

        return loggers;
    }
}
