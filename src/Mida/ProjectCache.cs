using System.Collections.Concurrent;
using System.Diagnostics;
using Mida.Store;

namespace Mida;

/// <summary>
/// Keys, hits and stored misses, with no MSBuild in it. <see cref="WalkAsync"/> serves the hits that need nothing built
/// before any build session exists, and <see cref="MidaCachePlugin"/> asks it for the rest while MSBuild builds.
/// </summary>
internal sealed class ProjectCache(GraphModel graph, KeyComputer computer, OutputCache cache, string repoRoot, bool readOnly)
{
    private static readonly Manifest NoOutputs = new([], [], []);

    // What MSBuild derives from an item's path, and refuses as metadata of a replayed item.
    private static readonly HashSet<string> DerivedMetadata = new(StringComparer.OrdinalIgnoreCase)
    {
        "FullPath", "RootDir", "Filename", "Extension", "RelativeDir", "Directory", "Identity", "ModifiedTime", "CreatedTime", "AccessedTime",
        "DefiningProjectFullPath", "DefiningProjectDirectory", "DefiningProjectName", "DefiningProjectExtension",
    };

    private readonly ConcurrentDictionary<string, CacheKey> keys = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<ProjectModel, Manifest> served = new();
    private readonly ConcurrentDictionary<ProjectModel, byte> absent = new();
    private readonly ConcurrentQueue<Task> saves = new();
    private readonly ConcurrentQueue<(bool Warning, string Text)> messages = new();
    private readonly ConcurrentDictionary<ProjectModel, long> missStarts = new();
    private long keyTicks;
    private long savedTicks;
    private int failures;
    private int hits;
    private int misses;

    public IReadOnlyDictionary<string, CacheKey> Keys => keys;

    public TimeSpan KeyTime => Stopwatch.GetElapsedTime(0, Volatile.Read(ref keyTicks));

    /// <summary>The stored build times of the hits, summed over projects, so not wall-clock time.</summary>
    public TimeSpan TimeSaved => TimeSpan.FromTicks(Volatile.Read(ref savedTicks));

    public int Failures => Volatile.Read(ref failures);

    public int Hits => Volatile.Read(ref hits);

    public int Misses => Volatile.Read(ref misses);

    // Any failure is a miss: failing the build would make the cache a new way to break CI. An allow-list let a store's
    // NotSupportedException or its own timeout fail the build with no message. Only the build's own cancellation passes.
    public static bool IsCacheFailure(Exception e, CancellationToken build) =>
        !(e is OperationCanceledException && build.IsCancellationRequested);

    /// <summary>
    /// Serves every hit whose dependencies all hit, dependencies first, so each reads the files of those before it.
    /// True when that was every project, and then nothing is left for MSBuild to do.
    /// </summary>
    public async Task<bool> WalkAsync()
    {
        var walked = new Dictionary<ProjectModel, Task<bool>>(graph.Projects.Count);
        foreach (var project in graph.Projects)
        {
            var dependencies = project.References.Select(d => walked[d]).ToArray();
            walked[project] = Task.Run(async () => (await Task.WhenAll(dependencies)).All(hit => hit) && await TryServeAsync(project));
        }

        return (await Task.WhenAll(walked.Values)).All(hit => hit);
    }

    private async Task<bool> TryServeAsync(ProjectModel project)
    {
        // A key that cannot be computed is left to the build session, which asks again and says why.
        if (!graph.IsKeyed(project) || ComputeKey(project, project.Targets, project.Properties, warn: null) is not { } key)
        {
            return false;
        }

        try
        {
            if (await TryRestoreAsync(project, key, CancellationToken.None) is { } manifest)
            {
                Hit(project, manifest);
                served[project] = manifest;
                return true;
            }
        }
        catch (Exception e) when (IsCacheFailure(e, CancellationToken.None))
        {
            messages.Enqueue((true, $"Cache entry for {project.Path} not used: {e.Message}"));
        }

        absent[project] = 0;
        return false;
    }

    /// <summary>The node MSBuild asks about: an inner build has its path and the framework as a global property.</summary>
    public bool TryGetProject(string path, string targetFramework, out ProjectModel project) => graph.TryGetProject(path, targetFramework, out project);

    /// <summary>True for a hit the walk served, whose files are in place.</summary>
    public bool TryGetServed(ProjectModel project, out Manifest manifest) => served.TryGetValue(project, out manifest!);

    /// <summary>True when the walk looked and found nothing usable, so the session need not look again.</summary>
    public bool IsAbsent(ProjectModel project) => absent.ContainsKey(project);

    /// <summary>Takes back a hit of the walk that MSBuild cannot replay.</summary>
    public void Revoke(ProjectModel project, Manifest manifest)
    {
        if (served.TryRemove(project, out _))
        {
            Interlocked.Decrement(ref hits);
            Interlocked.Add(ref savedTicks, -manifest.BuildTime.Ticks);
        }
    }

    /// <summary>Null when the key cannot be computed, which <paramref name="warn"/> hears unless it is null.</summary>
    public CacheKey? ComputeKey(ProjectModel project, IEnumerable<string> targets, IEnumerable<KeyValuePair<string, string>> properties, Action<string>? warn)
    {
        var start = Stopwatch.GetTimestamp();
        try
        {
            return keys[computer.Name(project)] = computer.Compute(project, targets, properties, keys);
        }
        catch (Exception e) when (IsCacheFailure(e, CancellationToken.None))
        {
            // A project without a key builds normally.
            if (warn is not null)
            {
                Interlocked.Increment(ref failures);
                warn($"No cache key for {project.Path}: {e.Message}");
            }

            return null;
        }
        finally
        {
            Interlocked.Add(ref keyTicks, Stopwatch.GetTimestamp() - start);
        }
    }

    /// <summary>Restores the outputs stored under the key. Null is a miss, and a failure throws.</summary>
    public async Task<Manifest?> TryRestoreAsync(ProjectModel project, CacheKey key, CancellationToken cancellationToken)
    {
        if (await cache.TryRestoreAsync(key.Value, manifest => Accept(project, manifest), cancellationToken) is not { } manifest)
        {
            return null;
        }

        // The walk has no MSBuild to ask, and MSBuild's own refusal comes as an ArgumentException with no message worth reading.
        if (manifest.Targets.Count == 0
            || manifest.Targets.Any(t => t.Items.Any(i => i.Spec.Length == 0 || i.Metadata.Keys.Any(DerivedMetadata.Contains))))
        {
            throw new InvalidDataException("MSBuild would not take its target results");
        }

        return manifest;
    }

    /// <summary>Counts a hit whose files <see cref="TryRestoreAsync"/> put in place, and removes what the entry does not list.</summary>
    public void Hit(ProjectModel project, Manifest manifest)
    {
        // A hit skips MSBuild's incremental clean, so a file of another revision would outlive it.
        if (!graph.Shares(project))
        {
            var obsolete = Restored(project);
            obsolete.UnionWith(ProjectOutputs.Written(project));
            cache.RemoveObsolete(manifest, obsolete.Where(f => project.Outputs(f) && ProjectOutputs.IsCached(Path.GetFileName(f)) && File.Exists(f)));
            File.WriteAllLines(project.Record!, Paths(manifest));
        }

        Interlocked.Increment(ref hits);
        Interlocked.Add(ref savedTicks, manifest.BuildTime.Ticks);
    }

    /// <summary>Counts a miss and clears the way for its build.</summary>
    public void Miss(ProjectModel project, Action<string> warn)
    {
        try
        {
            // MSBuild's clean reads a file list that holds nothing a hit restored, which this build would then store.
            if (!graph.Shares(project) && project.Record is { } record)
            {
                cache.RemoveObsolete(NoOutputs, Restored(project).Where(File.Exists));
                File.Delete(record);
            }
        }
        catch (Exception e) when (IsCacheFailure(e, CancellationToken.None))
        {
            warn($"Outputs of {project.Path} not cleared: {e.Message}");
        }

        Interlocked.Increment(ref misses);
        // A graph build asks once its dependencies are built, so this times the project's own build.
        missStarts[project] = Stopwatch.GetTimestamp();
    }

    /// <summary>Stores what a project built. <paramref name="capture"/> reads the target results, which only MSBuild can.</summary>
    public Task StoreAsync(string path, string targetFramework, bool succeeded, Func<ProjectModel, List<ManifestTarget>> capture, Action<string> warn, CancellationToken cancellationToken)
    {
        if (readOnly
            || !succeeded
            || !graph.TryGetProject(path, targetFramework, out var project)
            || !keys.TryGetValue(computer.Name(project), out var key))
        {
            return Task.CompletedTask;
        }

        var buildTime = missStarts.TryGetValue(project, out var start) ? Stopwatch.GetElapsedTime(start) : TimeSpan.Zero;
        List<ManifestTarget> targets;
        try
        {
            // Now, not in the background: MSBuild merges later target results into this BuildResult.
            targets = capture(project);
        }
        catch (Exception e) when (IsCacheFailure(e, cancellationToken))
        {
            warn($"Outputs of {path} not stored: {e.Message}");
            return Task.CompletedTask;
        }

        // A dependent may already write to a shared directory, so the scan must happen before this hook returns.
        if (graph.Shares(project))
        {
            return SaveAsync(project, key, targets, buildTime, cancellationToken);
        }

        // MSBuild waits for this hook holding its BuildManager lock, and the pool threads a save needs queue up
        // behind that lock, which stalled misses for up to a minute. See docs/notes/benchmarks.md
        // No token: MSBuild cancels it when the build ends, which is before the last saves do.
        saves.Enqueue(Task.Run(() => SaveAsync(project, key, targets, buildTime, CancellationToken.None), CancellationToken.None));
        return Task.CompletedTask;
    }

    /// <summary>Waits for the saves still running, then hands over what there is to say about them and about the walk.</summary>
    public async Task FlushAsync(Action<bool, string> say)
    {
        await Task.WhenAll(saves);
        while (messages.TryDequeue(out var message))
        {
            say(message.Warning, message.Text);
        }
    }

    private async Task SaveAsync(ProjectModel project, CacheKey key, List<ManifestTarget> targets, TimeSpan buildTime, CancellationToken cancellationToken)
    {
        try
        {
            var outputs = ProjectOutputs.Scan(project);
            var copies = computer.DependencyCopies(project, outputs);
            var stored = await cache.SaveAsync(
                key.Value,
                outputs.Where(f => !copies.ContainsKey(f)),
                // In a shared directory a dependency's file is its own source, and a restore would delete it before copying.
                [.. copies.Where(c => c.Key != c.Value).Select(c => new ManifestReference(Name(c.Key), Name(c.Value))).OrderBy(r => r.Path, StringComparer.Ordinal)],
                targets,
                buildTime,
                cancellationToken);
            if (!stored)
            {
                messages.Enqueue((true, $"Outputs of {project.Path} not stored: another build stored different outputs for this key, so the build is not deterministic"));
            }
        }
        catch (Exception e) when (IsCacheFailure(e, cancellationToken))
        {
            messages.Enqueue((true, $"Outputs of {project.Path} not stored: {e.Message}"));
        }
    }

    private string Name(string path) => computer.Relative(path);

    private IEnumerable<string> Paths(Manifest manifest) =>
        manifest.Files.Select(f => f.Path).Concat(manifest.References.Select(r => r.Path)).Select(p => Path.GetFullPath(p, repoRoot));

    /// <summary>
    /// Before any file of a hit moves: it may write only into the project's output directories, and what it writes is
    /// noted first, so a restore that fails halfway leaves nothing behind that a miss would not take back.
    /// </summary>
    private void Accept(ProjectModel project, Manifest manifest)
    {
        var paths = Paths(manifest).ToList();
        if (paths.FirstOrDefault(p => !project.Outputs(p)) is { } outside)
        {
            throw new InvalidDataException($"{outside} is outside the output directories of {project.Path}");
        }

        if (!graph.Shares(project))
        {
            var record = project.Record!;
            Directory.CreateDirectory(Path.GetDirectoryName(record)!);
            File.WriteAllLines(record, Restored(project).Union(paths, StringComparer.Ordinal));
        }
    }

    /// <summary>The files hits restored for the project since a build last ran, as far as they are still its outputs.</summary>
    private static HashSet<string> Restored(ProjectModel project) =>
        project.Record is { } record && File.Exists(record)
            ? File.ReadLines(record).Where(project.Outputs).ToHashSet(StringComparer.FromComparison(KeyComputer.PathComparison))
            : new HashSet<string>(StringComparer.FromComparison(KeyComputer.PathComparison));
}
