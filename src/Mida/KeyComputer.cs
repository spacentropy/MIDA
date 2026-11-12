using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Mida.Store;

namespace Mida;

internal sealed partial class KeyComputer(string repoRoot, IReadOnlyList<string> environment, ConcurrentDictionary<string, string> fileHashes)
{
    public static readonly StringComparison PathComparison =
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    // Say which solution started the build, not what to build. CurrentSolutionConfigurationContents lists every
    // project, so keeping it would invalidate the whole repo whenever a project joins the solution.
    private static readonly HashSet<string> IgnoredProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "BuildingSolutionFile",
        "CurrentSolutionConfigurationContents",
        "SolutionDir",
        "SolutionExt",
        "SolutionFileName",
        "SolutionName",
        "SolutionPath",
    };

    private readonly ConcurrentDictionary<string, string> referenceHashes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> inputLines = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<ProjectModel, HashSet<string>> outputNames = new();
    private readonly ConcurrentDictionary<ProjectModel, string?> assetHashes = new();
    private readonly string repoPrefix = WithSeparator(repoRoot);

    /// <summary>True for a global property that a key holds.</summary>
    public static bool IsKeyed(string property) => !IgnoredProperties.Contains(property);

    /// <summary>Must run after every dependency has finished: it reads their reference assemblies.</summary>
    public CacheKey Compute(
        ProjectModel project,
        IEnumerable<string> targets,
        IEnumerable<KeyValuePair<string, string>> globalProperties,
        IReadOnlyDictionary<string, CacheKey> dependencyKeys)
    {
        // Such a directory holds the project's sources, and at the repo's root its .git, which a hit would write over.
        var projectDirectory = WithSeparator(Path.GetDirectoryName(project.Path)!);
        if (project.OutputDirectories.FirstOrDefault(d => projectDirectory.StartsWith(d, PathComparison)) is { } holding)
        {
            throw new InvalidOperationException($"its output directory {holding} holds the project itself");
        }

        if (project.Record is null)
        {
            throw new InvalidOperationException("it has no obj/ to note what a hit restores");
        }

        var lines = new List<string>(environment) { $"project {Relative(project.Path)}" };
        lines.AddRange(targets.Select(t => $"target {t}"));
        lines.AddRange(globalProperties
            .Where(p => IsKeyed(p.Key))
            .Select(p => $"property {p.Key}={p.Value.Replace(repoRoot, "{RepoRoot}", PathComparison)}"));
        lines.AddRange(project.Inputs.Select(f => inputLines.GetOrAdd(f, InputLine)));

        if (project.AssetsFile is { } assets && File.Exists(assets))
        {
            lines.Add($"packages {PackageFingerprint.Compute(File.ReadAllBytes(assets))}");
        }

        AddDependencies(project, lines, dependencyKeys, []);
        return CacheKey.From(lines);
    }

    /// <summary>
    /// Maps files of a finished project's <c>bin/</c> to the dependency file they copy. Same relative path and same
    /// bytes: bytes alone would tie together unrelated files that happen to match, such as two empty ones.
    /// </summary>
    public Dictionary<string, string> DependencyCopies(ProjectModel project, IEnumerable<string> outputs)
    {
        var directory = project.OutDir;
        var closure = new HashSet<ProjectModel>();
        AddClosure(project, closure);
        var dependencies = closure.OrderBy(d => d.Path, StringComparer.Ordinal).ToList();

        // MSBuild lets the project's own item win a path, so a match there is a coincidence that a later edit can break.
        var own = project.OwnCopies;
        var copies = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var output in outputs.Where(o => o.StartsWith(directory, PathComparison)))
        {
            var name = Path.GetRelativePath(directory, output);
            if (own.Contains(name))
            {
                continue;
            }

            var source = dependencies
                .Where(d => OutputNames(d).Contains(name))
                .Select(d => Path.Combine(d.OutDir, name))
                .FirstOrDefault(file => HashFile(file) == HashFile(output));
            if (source is not null)
            {
                copies[output] = source;
            }
        }

        return copies;
    }

    private static void AddClosure(ProjectModel project, HashSet<ProjectModel> closure)
    {
        foreach (var dependency in project.References)
        {
            if (closure.Add(dependency))
            {
                AddClosure(dependency, closure);
            }
        }
    }

    private HashSet<string> OutputNames(ProjectModel project) => outputNames.GetOrAdd(project, static p =>
    {
        var directory = p.OutDir;
        return !Directory.Exists(directory)
            ? []
            :
            [
                .. Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                    .Where(f => ProjectOutputs.IsCached(Path.GetFileName(f)))
                    .Select(f => Path.GetRelativePath(directory, f)),
            ];
    });

    // Without the times, which are the sources' and so differ in every checkout.
    private string? AssetsHash(ProjectModel project)
    {
        var endpoints = OutputNames(project).Where(n => n.EndsWith(".staticwebassets.endpoints.json", StringComparison.Ordinal)).ToList();
        return endpoints.Count == 0 ? null : CacheKey.From(endpoints.Select(n =>
            $"{n} {Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(LastModified().Replace(File.ReadAllText(Path.Combine(project.OutDir, n)), string.Empty))))}")).Value;
    }

    [GeneratedRegex("\"Last-Modified\",\"Value\":\"[^\"]*\"")]
    private static partial Regex LastModified();

    public static string WithSeparator(string path)
    {
        var normalized = path.Replace('\\', Path.DirectorySeparatorChar);
        return Path.EndsInDirectorySeparator(normalized) ? normalized : normalized + Path.DirectorySeparatorChar;
    }

    private void AddDependencies(
        ProjectModel project,
        List<string> lines,
        IReadOnlyDictionary<string, CacheKey> dependencyKeys,
        HashSet<ProjectModel> seen)
    {
        foreach (var dependency in project.References)
        {
            var name = Name(dependency);
            if (project.BuildTimeReferences.Contains(dependency.Path))
            {
                // Its behaviour shapes this project's output, so its API alone says too little. Its key covers its own dependencies.
                // Before the seen check: a normal reference reached earlier must not hide this edge.
                lines.Add($"dependency-key {name} {dependencyKeys[name].Value}");

                // An outer node's key has only the API of its inner node, whose behaviour is what counts here.
                lines.AddRange(dependency.References
                    .Where(inner => dependency.CrossTargeting && inner.Path == dependency.Path)
                    .Select(inner => $"dependency-key {Name(inner)} {dependencyKeys[Name(inner)].Value}"));
                continue;
            }

            if (!seen.Add(dependency))
            {
                continue;
            }

            if (dependency.CrossTargeting)
            {
                // An outer build has no assembly, its inner builds do. All of them, which saves resolving the framework this project picks.
                AddDependencies(dependency, lines, dependencyKeys, seen);
                continue;
            }

            lines.Add($"dependency-ref {name} {ReferenceHash(dependency)}");

            // A file joining a dependency's bin/, such as a new satellite assembly, leaves its reference assembly alone.
            lines.Add($"dependency-files {name} {CacheKey.From(OutputNames(dependency)).Value}");

            // A host's manifests hold each static web asset's fingerprint and integrity, which change on a body edit of a
            // library's asset while its API and file names stay. See docs/notes/static-web-assets.md
            if (assetHashes.GetOrAdd(dependency, AssetsHash) is { } assets)
            {
                lines.Add($"dependency-assets {name} {assets}");
            }

            AddDependencies(dependency, lines, dependencyKeys, seen);
        }
    }

    private string ReferenceHash(ProjectModel project)
    {
        // Projects with ProduceReferenceAssembly off have no ref: then the implementation is all there is.
        if (project.TargetRefPath is not { } full)
        {
            return HashFile(project.TargetPath ?? throw new InvalidOperationException($"No reference assembly path for {project.Path}"));
        }

        return referenceHashes.GetOrAdd(full, file =>
        {
            try
            {
                return ReferenceAssemblyHash.Compute(file);
            }
            catch (BadImageFormatException)
            {
                return HashFile(file);
            }
        });
    }

    // The graph holds full paths, and none under the SDK, the package folder or a project's outputs.
    private string InputLine(string full)
    {
        var name = full.StartsWith(repoPrefix, PathComparison) ? Relative(full) : Path.GetFileName(full);
        return File.Exists(full) ? $"input {name} {HashFile(full)}" : $"missing {name}";
    }

    /// <summary>Null for no path, which is how MSBuild says a property is not set.</summary>
    public static string? AbsolutePath(string directory, string path) =>
        path.Length == 0 ? null : Path.GetFullPath(path.Replace('\\', Path.DirectorySeparatorChar), directory);

    /// <summary>The name a project or file has in keys, manifests and <c>--print-keys</c>.</summary>
    public string Relative(string path) => Path.GetRelativePath(repoRoot, path).Replace('\\', '/');

    /// <summary>The name of a graph node: an inner build shares its path with its outer build.</summary>
    public string Name(ProjectModel project) =>
        project.TargetFramework.Length == 0 ? Relative(project.Path) : $"{Relative(project.Path)} ({project.TargetFramework})";

    private string HashFile(string path) => fileHashes.GetOrAdd(path, OutputCache.Hash);
}
