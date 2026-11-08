using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;

namespace Mida.Store;

/// <summary>Stores and restores one project's outputs. Failures throw: the caller turns them into a miss.</summary>
public sealed class OutputCache(IObjectStore store, string repoRoot)
{
    private const int MaxUnixFileMode = 0xFFF;

    private readonly ConcurrentDictionary<string, byte> verified = new(StringComparer.Ordinal);

    public static string Hash(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>
    /// Null is a miss. A failure partway leaves restored files behind, which is safe: each is right for this key,
    /// and the miss builds over them.
    /// </summary>
    public async Task<Manifest?> TryRestoreAsync(string key, CancellationToken cancellationToken)
    {
        if (await store.GetAsync(ManifestKey(key), cancellationToken) is not { } json)
        {
            return null;
        }

        var manifest = JsonSerializer.Deserialize(json, ManifestJson.Default.Manifest)
            ?? throw new InvalidDataException($"Manifest {key} is empty");
        Validate(key, manifest);
        await Parallel.ForEachAsync(manifest.Files, cancellationToken, (file, token) => new ValueTask(RestoreAsync(file, token)));
        await Parallel.ForEachAsync(manifest.References, cancellationToken, (reference, _) =>
        {
            File.Copy(StorePath.Contained(repoRoot, reference.Source), Replaceable(reference.Path));
            return ValueTask.CompletedTask;
        });
        return manifest;
    }

    /// <summary>A hit skips MSBuild's incremental clean, so a file of another revision would outlive it.</summary>
    public void RemoveObsolete(Manifest manifest, IEnumerable<string> outputs)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(outputs);
        var current = manifest.Files.Select(f => f.Path).Concat(manifest.References.Select(r => r.Path)).ToHashSet(StringComparer.Ordinal);
        foreach (var output in outputs.Where(o => !current.Contains(Relative(o))))
        {
            Delete(output);
        }
    }

    public async Task SaveAsync(
        string key,
        IEnumerable<string> files,
        IReadOnlyList<ManifestReference> references,
        IReadOnlyList<ManifestTarget> targets,
        CancellationToken cancellationToken)
    {
        var stored = new ConcurrentBag<ManifestFile>();
        await Parallel.ForEachAsync(files, cancellationToken, async (file, token) =>
        {
            var hash = Hash(file);
            await store.TryCreateFileAsync(BlobKey(hash), file, token);
            stored.Add(new ManifestFile(Relative(file), hash, OperatingSystem.IsWindows() ? null : (int)File.GetUnixFileMode(file)));
        });

        // Blobs first: a reader that finds a manifest relies on them. Sorted, so a deterministic build writes identical bytes.
        var manifest = new Manifest([.. stored.OrderBy(f => f.Path, StringComparer.Ordinal)], references, targets);
        await store.TryCreateAsync(ManifestKey(key), JsonSerializer.SerializeToUtf8Bytes(manifest, ManifestJson.Default.Manifest), cancellationToken);
    }

    private static string BlobKey(string hash) => $"v1/cas/{hash[..2]}/{hash}";

    private static string ManifestKey(string key) => $"v1/ac/{key}";

    // Windows refuses to delete a read-only file.
    private static void Delete(string path)
    {
        if (OperatingSystem.IsWindows() && File.Exists(path))
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }

        File.Delete(path);
    }

    // Before the first file moves. The JSON options reject nulls in properties, which leaves those inside collections.
    private void Validate(string key, Manifest manifest)
    {
        var valid = manifest.Files.All(f => f is not null
                && f.Hash.Length == SHA256.HashSizeInBytes * 2
                && f.Hash.All(char.IsAsciiHexDigitLower)
                && f.Mode is null or >= 0 and <= MaxUnixFileMode)
            && manifest.References.All(r => r is not null)
            && manifest.Targets.All(t => t is not null
                && t.Name.Length > 0
                && t.Items.All(i => i is not null && i.Metadata.Values.All(v => v is not null)))
            // MSBuild hangs on a result that holds a target twice.
            && manifest.Targets.DistinctBy(t => t.Name, StringComparer.OrdinalIgnoreCase).Count() == manifest.Targets.Count
            && DistinctFiles(manifest);
        if (!valid)
        {
            throw new InvalidDataException($"Manifest {key} is malformed");
        }

        foreach (var path in manifest.Files.Select(f => f.Path).Concat(manifest.References.SelectMany(r => new[] { r.Path, r.Source })))
        {
            StorePath.Contained(repoRoot, path);
        }
    }

    // A path written twice gets whichever copy lands last, and a path that is another's directory fails one of them.
    private static bool DistinctFiles(Manifest manifest)
    {
        var paths = new HashSet<string>(OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
        var all = manifest.Files.Select(f => f.Path).Concat(manifest.References.Select(r => r.Path)).ToList();
        return all.All(paths.Add) && !all.Any(p => Parents(p).Any(paths.Contains));
    }

    private static IEnumerable<string> Parents(string path)
    {
        var parts = path.Split('/');
        return Enumerable.Range(1, parts.Length - 1).Select(i => string.Join('/', parts[..i]));
    }

    private async Task RestoreAsync(ManifestFile file, CancellationToken cancellationToken)
    {
        var destination = Replaceable(file.Path);
        if (!await store.TryGetFileAsync(BlobKey(file.Hash), destination, cancellationToken))
        {
            throw new FileNotFoundException($"Blob {file.Hash} is missing", file.Path);
        }

        // The same DLL lands in many bin/ folders, so checking each blob once per run saves most of the hashing. Each check
        // reads its own copy and only a pass is kept: another project's copy may be gone, and a failure must not stick.
        if (!verified.ContainsKey(file.Hash))
        {
            if (Hash(destination) != file.Hash)
            {
                // shortcut: the blob stays, so this key misses until the cache is deleted. Upgrade when a store can delete.
                Delete(destination);
                throw new InvalidDataException($"Blob {file.Hash} is corrupt");
            }

            verified.TryAdd(file.Hash, 0);
        }

        if (file.Mode is { } mode && !OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(destination, (UnixFileMode)mode);
        }
    }

    // Deleted first: a build can leave a read-only file there.
    private string Replaceable(string path)
    {
        var full = StorePath.Contained(repoRoot, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        Delete(full);
        return full;
    }

    private string Relative(string file)
    {
        var relative = Path.GetRelativePath(repoRoot, StorePath.Contained(repoRoot, file));
        return relative.Replace('\\', '/');
    }
}
