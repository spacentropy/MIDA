using System.Collections.Concurrent;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mida.Store;

namespace Mida;

/// <summary>
/// Evaluated graphs in the store, so a run whose project files are unchanged needs neither MSBuild's evaluation nor a
/// restore. The graphs of one solution form a chain: each key comes from the one before and this checkout's answers
/// to the questions of the graph stored there. See docs/notes/stored-graph.md
/// </summary>
internal sealed class GraphStore(IObjectStore store, OutputCache blobs, Checkout checkout)
{
    private readonly PathTokens tokens = checkout.Tokens;
    private readonly string repoRoot = checkout.RepoRoot;
    private readonly string repoPrefix = KeyComputer.WithSeparator(checkout.RepoRoot);
    private readonly ConcurrentDictionary<string, string> hashes = checkout.Hashes;
    private readonly IReadOnlyDictionary<string, string> variables = checkout.Variables;

    /// <summary>Where the chain of a solution starts. Whatever an evaluation depends on without asking is in here.</summary>
    public string FirstKey(string solution, IReadOnlyDictionary<string, string> globalProperties, string sdkVersion)
    {
        string[] lines =
        [
            // A build of mida that reads a project differently must not meet the graphs of another.
            $"mida {typeof(GraphStore).Module.ModuleVersionId}",
            $"sdk {sdkVersion}",
            $"platform {RuntimeInformation.RuntimeIdentifier}",
            $"solution {Path.GetRelativePath(repoRoot, solution).Replace('\\', '/')} {OutputCache.Hash(solution)}",
            .. globalProperties.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"property {p.Key}={tokens.Tokenize(p.Value)}"),
        ];
        return Hash(string.Join('\n', lines));
    }

    /// <summary>The key after one whose graph does not fit this checkout.</summary>
    public string NextKey(string key, GraphSnapshot unfit) => Hash($"{key}\n{Questions.Answers(unfit.Questions, tokens, hashes, variables)}");

    /// <summary>Null when nothing is stored under the key.</summary>
    public async Task<GraphSnapshot?> GetAsync(string key, CancellationToken cancellationToken)
    {
        if (await store.GetAsync(ObjectKey(key), cancellationToken) is not { } compressed)
        {
            return null;
        }

        using var json = new GZipStream(new MemoryStream(compressed), CompressionMode.Decompress);
        return await JsonSerializer.DeserializeAsync(json, GraphJson.Default.GraphSnapshot, cancellationToken)
            ?? throw new InvalidDataException($"The graph {key} is empty");
    }

    /// <summary>The first file or variable that differs from what the graph was evaluated with, or null.</summary>
    public string? ChangedSource(GraphSnapshot snapshot) => Questions.ChangedSource(snapshot.Questions, tokens, hashes, variables);

    /// <summary>The first path that exists or lists differently, or null. Asked with the restore outputs in place.</summary>
    public string? ChangedProbe(GraphSnapshot snapshot) => Questions.ChangedProbe(snapshot.Questions, tokens);

    /// <summary>
    /// True when a restore would have nothing to resolve or download, and this machine's paths can be written into its
    /// outputs as they are: a path that XML or JSON would escape has another spelling there.
    /// </summary>
    public bool CanSkipRestore(GraphSnapshot snapshot) =>
        snapshot.Restore.Reusable && tokens.Plain && snapshot.Restore.Packages.All(p => File.Exists(tokens.DetokenizePath(p)));

    /// <summary>Writes the restore outputs with this checkout's paths. False when a blob is gone.</summary>
    public async Task<bool> MaterializeAsync(GraphSnapshot snapshot, CancellationToken cancellationToken)
    {
        var complete = true;
        await Parallel.ForEachAsync(snapshot.Restore.Files, cancellationToken, async (file, token) =>
        {
            var path = Restored(file.Path);
            if (await blobs.GetBlobAsync(file.Hash, token) is not { } stored || tokens.Detokenize(stored) is not { } content)
            {
                complete = false;
                return;
            }

            // A file that is already right keeps its time, which an incremental build reads.
            if (!(File.Exists(path) && new FileInfo(path).Length == content.Length && (await File.ReadAllBytesAsync(path, token)).AsSpan().SequenceEqual(content)))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllBytesAsync(path, content, token);
            }
        });
        return complete;
    }

    /// <summary>
    /// After a restore that ran: true when it generated the props and targets the graph was evaluated with. The assets
    /// file is not compared, because a key reads the one on disk.
    /// </summary>
    public bool RestoredSameImports(GraphSnapshot snapshot)
    {
        var stored = snapshot.Restore.Files.ToDictionary(f => Restored(f.Path), f => f.Hash, StringComparer.Ordinal);
        return snapshot.Projects
            .Where(p => p.ExtensionsPath is not null)
            .SelectMany(p => ImportsOf(tokens.DetokenizePath(p.ExtensionsPath!), tokens.DetokenizePath(p.Path)))
            .Distinct(StringComparer.Ordinal)
            .All(import => Current(import) == stored.GetValueOrDefault(import));
    }

    /// <summary>Completes an evaluated graph with what the restore before it wrote, and stores it. Failures are said, not thrown.</summary>
    public async Task<string?> TryStoreAsync(string key, Evaluated evaluated, CancellationToken cancellationToken)
    {
        try
        {
            var restore = await CaptureAsync(evaluated, cancellationToken);
            // Compressed: the same few directories are in every path, and a cold runner fetches this before anything else.
            using var compressed = new MemoryStream();
            using (var json = new GZipStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            {
                await JsonSerializer.SerializeAsync(json, new GraphSnapshot(evaluated.Environment, evaluated.Projects, evaluated.Questions, restore), GraphJson.Default.GraphSnapshot, cancellationToken);
            }

            await store.TryCreateAsync(ObjectKey(key), compressed.ToArray(), cancellationToken);
            return null;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return e.Message;
        }
    }

    private static string ObjectKey(string key) => $"v1/graph/{key}";

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static string[] ImportsOf(string extensionsPath, string project) =>
    [
        Path.Combine(extensionsPath, Path.GetFileName(project) + ".nuget.g.props"),
        Path.Combine(extensionsPath, Path.GetFileName(project) + ".nuget.g.targets"),
    ];

    private string? Current(string path) =>
        File.Exists(path) && tokens.Tokenize(File.ReadAllBytes(path)) is { } content ? Convert.ToHexStringLower(SHA256.HashData(content)) : null;

    // A stored graph may write restore outputs under the repo and nothing else.
    private string Restored(string relative)
    {
        var full = Path.GetFullPath(relative, repoRoot);
        return full.StartsWith(repoPrefix, KeyComputer.PathComparison) && ProjectOutputs.IsRestored(Path.GetFileName(full))
            ? full
            : throw new InvalidDataException($"{relative} is no restore output in {repoRoot}");
    }

    private async Task<SnapshotRestore> CaptureAsync(Evaluated evaluated, CancellationToken cancellationToken)
    {
        // Whatever makes the restore one that a later run has to repeat. Only its being empty is read. A path that XML or
        // JSON escapes is not found where it is written escaped, and the file would keep this machine's spelling of it.
        var unusable = new ConcurrentBag<string>();
        if (evaluated.RestoreNotReusable is { } reason)
        {
            unusable.Add(reason);
        }

        if (!tokens.Plain)
        {
            unusable.Add("a root needs escaping");
        }

        var files = new ConcurrentBag<RestoreFile>();
        var packages = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        var projects = evaluated.Projects
            .Where(p => p.ExtensionsPath is not null)
            .Select(p => (Extensions: tokens.DetokenizePath(p.ExtensionsPath!), Path: tokens.DetokenizePath(p.Path), Assets: p.AssetsFile is null ? null : tokens.DetokenizePath(p.AssetsFile)))
            .DistinctBy(p => (p.Extensions, p.Path));
        await Parallel.ForEachAsync(projects, cancellationToken, async (project, token) =>
        {
            var assets = Path.Combine(project.Extensions, "project.assets.json");
            if (!File.Exists(assets))
            {
                // Nothing was restored for this project, so nothing has to come back.
                return;
            }

            // Elsewhere, a stored graph would have to write outside the repo or guess which file a project reads.
            if (!project.Extensions.StartsWith(repoPrefix, KeyComputer.PathComparison) || !assets.Equals(project.Assets, KeyComputer.PathComparison))
            {
                unusable.Add(assets);
                return;
            }

            foreach (var file in ImportsOf(project.Extensions, project.Path).Append(assets).Where(File.Exists))
            {
                var content = await File.ReadAllBytesAsync(file, token);
                if (tokens.Tokenize(content) is not { } relocatable)
                {
                    unusable.Add(file);
                    continue;
                }

                files.Add(new RestoreFile(Path.GetRelativePath(repoRoot, file).Replace('\\', '/'), await blobs.SaveBlobAsync(relocatable, token)));
                if (file == assets)
                {
                    // A floating version resolves again on every restore, and its range holds a star.
                    if (content.AsSpan().Contains((byte)'*'))
                    {
                        unusable.Add(file);
                    }
                }
            }

            // NuGet's own list of what must be in the package folder for a restore to have nothing to do.
            var cache = Path.Combine(project.Extensions, "project.nuget.cache");
            if (File.Exists(cache))
            {
                foreach (var package in Strings(await File.ReadAllBytesAsync(cache, token), "expectedPackageFiles"))
                {
                    packages.TryAdd(tokens.TokenizePath(package), 0);
                }
            }
            else
            {
                unusable.Add(cache);
            }
        });

        return new SnapshotRestore(
            unusable.IsEmpty,
            [.. packages.Keys.Order(StringComparer.Ordinal)],
            [.. files.OrderBy(f => f.Path, StringComparer.Ordinal)]);
    }

    /// <summary>The strings of the array a JSON document holds under the names, or none.</summary>
    private static List<string> Strings(byte[] json, params string[] names)
    {
        using var document = JsonDocument.Parse(json);
        var element = document.RootElement;
        foreach (var name in names)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out element))
            {
                return [];
            }
        }

        return element.ValueKind == JsonValueKind.Array
            ? [.. element.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!)]
            : [];
    }
}
