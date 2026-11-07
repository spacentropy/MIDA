using System.Collections.Concurrent;
using Microsoft.Build.Execution;
using Microsoft.Build.Graph;
using Microsoft.Build.Prediction;
using Mida.Store;

namespace Mida;

internal sealed class KeyComputer(string repoRoot, IReadOnlyList<string> excludedRoots, IReadOnlyList<string> environment)
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

    private static readonly string[] ExcludedRootProperties = ["NuGetPackageRoot", "BaseIntermediateOutputPath", "BaseOutputPath"];

    private readonly ConcurrentDictionary<string, string> fileHashes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string?> inputLines = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<ProjectGraphNode, HashSet<string>> buildTimeReferences = new();
    private readonly ConcurrentDictionary<ProjectGraphNode, HashSet<string>> outputNames = new();
    private readonly string repoPrefix = WithSeparator(repoRoot);

    /// <summary>
    /// What a key must not read: the SDK and packages, which the key covers by version, and every project's outputs.
    /// </summary>
    public static List<string> ExcludedRoots(ProjectGraph graph, string dotnetRoot)
    {
        ArgumentNullException.ThrowIfNull(graph);
        return
        [
            WithSeparator(dotnetRoot),
            .. graph.ProjectNodes
                .SelectMany(n => ExcludedRootProperties.Select(p => AbsolutePath(n.ProjectInstance, n.ProjectInstance.GetPropertyValue(p))))
                .OfType<string>()
                .Select(WithSeparator)
                .Distinct(StringComparer.Ordinal),
        ];
    }

    /// <summary>Must run after every dependency has finished: it reads their reference assemblies.</summary>
    public CacheKey Compute(
        ProjectGraphNode node,
        ProjectPredictions predictions,
        IEnumerable<string> targets,
        IEnumerable<KeyValuePair<string, string>> globalProperties,
        IReadOnlyDictionary<string, CacheKey> dependencyKeys)
    {
        var project = node.ProjectInstance;
        var lines = new List<string>(environment) { $"project {Relative(project.FullPath)}" };
        lines.AddRange(targets.Select(t => $"target {t}"));
        lines.AddRange(globalProperties
            .Where(p => !IgnoredProperties.Contains(p.Key))
            .Select(p => $"property {p.Key}={p.Value.Replace(repoRoot, "{RepoRoot}", PathComparison)}"));
        lines.AddRange(predictions.InputFiles.Select(f => inputLines.GetOrAdd(f.Path, InputLine)).OfType<string>());

        if (AbsolutePath(project, project.GetPropertyValue("ProjectAssetsFile")) is { } assets && File.Exists(assets))
        {
            lines.Add($"packages {PackageFingerprint.Compute(File.ReadAllBytes(assets))}");
        }

        AddDependencies(node, lines, dependencyKeys, []);
        return CacheKey.From(lines);
    }

    /// <summary>
    /// Maps files of a finished project's <c>bin/</c> to the dependency file they copy. Same relative path and same
    /// bytes: bytes alone would tie together unrelated files that happen to match, such as two empty ones.
    /// </summary>
    public Dictionary<string, string> DependencyCopies(ProjectGraphNode node, IEnumerable<string> outputs)
    {
        ArgumentNullException.ThrowIfNull(node);
        var directory = OutputDirectory(node.ProjectInstance);
        var closure = new HashSet<ProjectGraphNode>();
        AddClosure(node, closure);
        var dependencies = closure.OrderBy(d => d.ProjectInstance.FullPath, StringComparer.Ordinal).ToList();

        var copies = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var output in outputs.Where(o => o.StartsWith(directory, PathComparison)))
        {
            var name = Path.GetRelativePath(directory, output);
            var source = dependencies
                .Where(d => OutputNames(d).Contains(name))
                .Select(d => Path.Combine(OutputDirectory(d.ProjectInstance), name))
                .FirstOrDefault(file => HashFile(file) == HashFile(output));
            if (source is not null)
            {
                copies[output] = source;
            }
        }

        return copies;
    }

    private static void AddClosure(ProjectGraphNode node, HashSet<ProjectGraphNode> closure)
    {
        foreach (var dependency in node.ProjectReferences)
        {
            if (closure.Add(dependency))
            {
                AddClosure(dependency, closure);
            }
        }
    }

    private static string OutputDirectory(ProjectInstance project) =>
        WithSeparator(AbsolutePath(project, project.GetPropertyValue("OutDir")) ?? project.Directory);

    private HashSet<string> OutputNames(ProjectGraphNode node) => outputNames.GetOrAdd(node, static n =>
    {
        var directory = OutputDirectory(n.ProjectInstance);
        return !Directory.Exists(directory)
            ? []
            :
            [
                .. Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                    .Where(f => ProjectOutputs.IsCached(Path.GetFileName(f)))
                    .Select(f => Path.GetRelativePath(directory, f)),
            ];
    });

    public static string WithSeparator(string path)
    {
        var normalized = path.Replace('\\', Path.DirectorySeparatorChar);
        return Path.EndsInDirectorySeparator(normalized) ? normalized : normalized + Path.DirectorySeparatorChar;
    }

    private void AddDependencies(
        ProjectGraphNode node,
        List<string> lines,
        IReadOnlyDictionary<string, CacheKey> dependencyKeys,
        HashSet<ProjectGraphNode> seen)
    {
        var buildTime = buildTimeReferences.GetOrAdd(node, BuildTimeReferences);
        foreach (var dependency in node.ProjectReferences)
        {
            if (!seen.Add(dependency))
            {
                continue;
            }

            var name = Relative(dependency.ProjectInstance.FullPath);
            if (buildTime.Contains(dependency.ProjectInstance.FullPath))
            {
                // Its behaviour shapes this project's output, so its API alone says too little. Its key covers its own dependencies.
                lines.Add($"dependency-key {name} {dependencyKeys[name].Value}");
                continue;
            }

            lines.Add($"dependency-ref {name} {HashFile(ReferenceAssembly(dependency.ProjectInstance))}");

            // A file joining a dependency's bin/, such as a new satellite assembly, leaves its reference assembly alone.
            lines.Add($"dependency-files {name} {CacheKey.From(OutputNames(dependency)).Value}");
            AddDependencies(dependency, lines, dependencyKeys, seen);
        }
    }

    private static HashSet<string> BuildTimeReferences(ProjectGraphNode node) =>
    [
        .. node.ProjectInstance.GetItems("ProjectReference")
            .Where(r => r.GetMetadataValue("OutputItemType").Length > 0
                || r.GetMetadataValue("ReferenceOutputAssembly").Equals("false", StringComparison.OrdinalIgnoreCase))
            .Select(r => AbsolutePath(node.ProjectInstance, r.EvaluatedInclude))
            .OfType<string>(),
    ];

    private static string ReferenceAssembly(ProjectInstance project)
    {
        // Projects with ProduceReferenceAssembly off have no ref: then the implementation is all there is.
        var path = project.GetPropertyValue("TargetRefPath");
        return AbsolutePath(project, path.Length > 0 ? path : project.GetPropertyValue("TargetPath"))
            ?? throw new InvalidOperationException($"No reference assembly path for {project.FullPath}");
    }

    private string? InputLine(string path)
    {
        var full = Path.GetFullPath(path);
        if (excludedRoots.Any(root => full.StartsWith(root, PathComparison)))
        {
            return null;
        }

        var name = full.StartsWith(repoPrefix, PathComparison) ? Relative(full) : Path.GetFileName(full);
        return File.Exists(full) ? $"input {name} {HashFile(full)}" : $"missing {name}";
    }

    public static string? AbsolutePath(ProjectInstance project, string path) =>
        path.Length == 0 ? null : Path.GetFullPath(path.Replace('\\', Path.DirectorySeparatorChar), project.Directory);

    private string Relative(string path) => Path.GetRelativePath(repoRoot, path).Replace('\\', '/');

    private string HashFile(string path) => fileHashes.GetOrAdd(path, OutputCache.Hash);
}
