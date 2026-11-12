using System.Globalization;
using System.Runtime.CompilerServices;
using Google.Apis.Auth.OAuth2;
using Mida.Store;

namespace Mida;

/// <param name="sdkProblem">Says why the loaded SDK is the wrong one, or null. Nothing is written before it has answered.</param>
internal sealed class BuildRunner(BuildOptions options, string sdkVersion, string sdkPath, Task<string?> sdkProblem)
{
    // A graph that does not fit sends the search to one more key. Checkouts that differ in this many ways have no stored graph.
    private const int MaxGraphs = 8;

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
        var checkout = Checkout.Of(options.Solution, sdkPath);
        var (repoRoot, tokens) = (checkout.RepoRoot, checkout.Tokens);
        var store = Store();
        var outputs = new OutputCache(store, repoRoot, ProjectOutputs.Relocation(tokens));
        var graphs = new GraphStore(store, outputs, checkout);

        IBuildHost? host = null;
        try
        {
            Phase.Mark("start");
            GraphModel? graph = null;
            string? free = null;
            var restored = false;
            var source = "Graph from the store, restore skipped";
            var why = options.Evaluate ? "--evaluate" : "none is stored";
            try
            {
                var key = options.Evaluate ? null : graphs.FirstKey(options.Solution, globalProperties, sdkVersion);
                for (var searched = 0; key is not null && graph is null && searched < MaxGraphs; searched++)
                {
                    if (graphs.GetAsync(key, CancellationToken.None).GetAwaiter().GetResult() is not { } candidate)
                    {
                        free = key;
                        break;
                    }

                    var changed = graphs.ChangedSource(candidate);
                    if (changed is null)
                    {
                        Phase.Mark("finding a stored graph");
                        if (sdkProblem.GetAwaiter().GetResult() is { } problem)
                        {
                            Console.Error.WriteLine(problem);
                            return 1;
                        }

                        Phase.Mark("waiting for the SDK check");

                        // Once a restore has run, its outputs are the ones to keep.
                        if (restored || !graphs.CanSkipRestore(candidate) || !graphs.MaterializeAsync(candidate, CancellationToken.None).GetAwaiter().GetResult())
                        {
                            if (!restored && !(host ??= CreateHost(checkout)).Restore())
                            {
                                return 1;
                            }

                            restored = true;
                            source = "Graph from the store";
                            changed = graphs.RestoredSameImports(candidate) ? null : "what restore generates";
                        }

                        changed ??= graphs.ChangedProbe(candidate);
                    }

                    if (changed is null)
                    {
                        graph = new GraphModel(candidate.Environment, candidate.Projects, tokens);
                    }
                    else
                    {
                        why = $"{Shown(changed, tokens, repoRoot)} changed";
                        key = graphs.NextKey(key, candidate);
                    }
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Like any failure of the cache: the build goes on without it.
                (graph, free, why) = (null, null, $"the stored graph could not be used, {e.Message}");
            }

            Phase.Mark("restore outputs and the stored graph's questions");
            Task<string?>? storing = null;
            if (graph is null)
            {
                if (sdkProblem.GetAwaiter().GetResult() is { } problem)
                {
                    Console.Error.WriteLine(problem);
                    return 1;
                }

                host ??= CreateHost(checkout);
                if (!restored && !host.Restore())
                {
                    return 1;
                }

                Phase.Mark("restore");
                var evaluated = host.Evaluate();
                graph = new GraphModel(evaluated.Environment, evaluated.Projects, tokens);
                source = $"Graph evaluated: {why}";
                if (evaluated.NotStorable is { } reason)
                {
                    source += $". Not stored: {reason}";
                }
                else if (free is not null && !options.ReadOnly)
                {
                    // Beside the build: it reads what restore wrote, which no build changes.
                    storing = Task.Run(() => graphs.TryStoreAsync(free, evaluated, CancellationToken.None));
                }
            }

            var cache = new ProjectCache(graph, new KeyComputer(repoRoot, graph.Environment, checkout.Hashes), outputs, repoRoot, options.ReadOnly);
            var succeeded = cache.WalkAsync().GetAwaiter().GetResult();
            Phase.Mark("hits");
            succeeded = succeeded || (host ??= CreateHost(checkout)).Build(cache);
            Phase.Mark("build");

            // Ends the build session, which waits for the saves and says what they have to say.
            var ending = host;
            host = null;
            ending?.Dispose();
            Phase.Mark("end of the build session");
            cache.FlushAsync((warning, text) => Console.WriteLine($"{(warning ? "warning" : "mida")}: {text}")).GetAwaiter().GetResult();
            if (storing?.GetAwaiter().GetResult() is { } failure)
            {
                source += $". Not stored: {failure}";
            }

            Phase.Mark("storing the graph");
            Phase.End();
            Report(cache, source);
            return succeeded ? 0 : 1;
        }
        finally
        {
            host?.Dispose();
        }
    }

    // Not inlined, and the only place that names the host: Run must not load MSBuild for a build that needs none.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private IBuildHost CreateHost(Checkout checkout) => MSBuildHost.Create(options, globalProperties, sdkVersion, checkout);

    private static string Shown(string question, PathTokens tokens, string repoRoot)
    {
        var path = tokens.DetokenizePath(question);
        return path.StartsWith(KeyComputer.WithSeparator(repoRoot), KeyComputer.PathComparison) ? Path.GetRelativePath(repoRoot, path) : path;
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

    private void Report(ProjectCache cache, string source)
    {
        if (options.PrintKeys)
        {
            foreach (var (project, key) in cache.Keys.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                Console.WriteLine($"{key.Value} {project}");
            }
        }

        if (options.KeyDetailsDirectory is { } directory)
        {
            foreach (var (project, key) in cache.Keys)
            {
                // Mirrors the repo layout: flattening the path would let two projects share one file.
                var file = Path.Combine(directory, project + ".key");
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllLines(file, key.Lines);
            }
        }

        Console.WriteLine(source);
        var seconds = cache.KeyTime.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture);
        Console.WriteLine($"{cache.Keys.Count} keys in {seconds} s, {cache.Failures} not computable");
        var saved = cache.TimeSaved.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture);
        Console.WriteLine($"{cache.Hits} hits, {cache.Misses} misses, {saved} s of project build time saved");
    }
}
