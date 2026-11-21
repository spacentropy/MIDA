using System.IO.Enumeration;

namespace Mida;

/// <summary>
/// A graph node as keys and the cache read it, with this checkout's paths. It comes from a <see cref="SnapshotProject"/>
/// whether MSBuild evaluated the graph in this run or an earlier one did, so both read the same thing.
/// </summary>
internal sealed class ProjectModel(SnapshotProject stored, PathTokens tokens, IReadOnlyList<ProjectModel> references)
{
    public string Path { get; } = tokens.DetokenizePath(stored.Path);

    public IReadOnlyList<string> Targets => stored.Targets;

    public IReadOnlyDictionary<string, string> Properties { get; } = stored.Properties.ToDictionary(p => p.Key, p => tokens.Detokenize(p.Value), StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> Inputs { get; } = [.. stored.Inputs.Select(tokens.DetokenizePath)];

    public string? AssetsFile { get; } = Place(stored.AssetsFile, tokens);

    /// <summary>The nodes this one references, each of them before it in the graph.</summary>
    public IReadOnlyList<ProjectModel> References => references;

    public HashSet<string> BuildTimeReferences { get; } = stored.BuildTimeReferences.Select(tokens.DetokenizePath).ToHashSet(StringComparer.Ordinal);

    public bool CrossTargeting => stored.CrossTargeting;

    public string? NoKey => stored.NoKey;

    /// <summary>How a test project runs, as <see cref="SnapshotProject.Test"/> says. Null for any other project.</summary>
    public string? Test => stored.Test;

    public IReadOnlyList<string> TestCategories => stored.TestCategories;

    public bool StoreTestPasses => stored.StoreTestPasses;

    /// <summary>The global property that tells an inner build from its outer build. Empty when not set.</summary>
    public string TargetFramework => Properties.GetValueOrDefault("TargetFramework", string.Empty);

    public string? TargetRefPath { get; } = Place(stored.TargetRefPath, tokens);

    public string? TargetPath { get; } = Place(stored.TargetPath, tokens);

    public string OutDir { get; } = tokens.DetokenizePath(stored.OutDir);

    public IReadOnlyList<string> OutputDirectories { get; } = [.. stored.OutputDirectories.Select(tokens.DetokenizePath)];

    public IReadOnlyList<string> OwnedRoots { get; } = [.. stored.OwnedRoots.Select(tokens.DetokenizePath)];

    public HashSet<string> OwnCopies { get; } = stored.OwnCopies.ToHashSet(StringComparer.FromComparison(KeyComputer.PathComparison));

    public string? PackageRoot { get; } = Place(stored.PackageRoot, tokens);

    public string? ExtensionsPath { get; } = Place(stored.ExtensionsPath, tokens);

    /// <summary>Where mida notes the files a hit restored, the only ones besides MSBuild's own that it deletes again.</summary>
    public string? Record => ExtensionsPath is null ? null
        : System.IO.Path.Combine(ExtensionsPath, $"{System.IO.Path.GetFileName(Path)}{(TargetFramework.Length > 0 ? $".{TargetFramework}" : string.Empty)}.mida-restored.txt");

    /// <summary>True for a path in one of the output directories.</summary>
    public bool Outputs(string path) => OutputDirectories.Any(d => path.StartsWith(d, KeyComputer.PathComparison));

    /// <summary>True for a path in bin/ or obj/, which hold nothing but what builds write.</summary>
    public bool Owns(string path) => OwnedRoots.Any(o => path.StartsWith(o, KeyComputer.PathComparison));

    /// <summary>The project's directory, with a separator at the end.</summary>
    public string Directory { get; } = KeyComputer.WithSeparator(System.IO.Path.GetDirectoryName(tokens.DetokenizePath(stored.Path))!);

    /// <summary>
    /// True for a path under the project's directory that MidaSourceOutputs names, or that is under one it names. A
    /// pattern goes through FileSystemName, where * matches / too. It cannot name anything outside the directory.
    /// </summary>
    public bool DeclaresSource(string path)
    {
        if (stored.SourceOutputs.Count == 0 || !path.StartsWith(Directory, KeyComputer.PathComparison))
        {
            return false;
        }

        var relative = path[Directory.Length..].Replace('\\', '/');
        var ignoreCase = KeyComputer.PathComparison == StringComparison.OrdinalIgnoreCase;
        return stored.SourceOutputs.Any(p => FileSystemName.MatchesSimpleExpression(p, relative, ignoreCase)
            || relative.StartsWith(p + "/", KeyComputer.PathComparison));
    }

    private static string? Place(string? path, PathTokens tokens) => path is null ? null : tokens.DetokenizePath(path);
}

internal sealed class GraphModel
{
    private readonly Dictionary<(string Path, string TargetFramework), ProjectModel> keyed = [];
    private readonly List<(ProjectModel Project, string Why)> unkeyed = [];
    private readonly HashSet<ProjectModel> sharing;

    public GraphModel(IReadOnlyList<string> environment, IReadOnlyList<SnapshotProject> stored, PathTokens tokens)
    {
        Environment = environment;
        var projects = new List<ProjectModel>(stored.Count);
        foreach (var project in stored)
        {
            // An index at or past its own would make a cycle, which no graph MSBuild evaluated has.
            if (project.References.Any(r => r < 0 || r >= projects.Count))
            {
                throw new InvalidDataException($"The stored graph references a project out of order from {project.Path}");
            }

            projects.Add(new ProjectModel(project, tokens, [.. project.References.Select(r => projects[r])]));
        }

        Projects = projects;

        // A project with several inner nodes is multi-targeted, which is out of scope: it stays a plain build. One that
        // names a single framework in TargetFrameworks has an outer node and one inner node, and both get keys.
        foreach (var nodes in projects.GroupBy(p => p.Path, StringComparer.Ordinal).Select(g => g.ToList()))
        {
            var outer = nodes.Count(p => p.CrossTargeting);

            // What the evaluation found first, such as "packs on build": see GraphEvaluation.NoKey.
            var why = nodes.Select(p => p.NoKey).FirstOrDefault(n => n is not null)
                ?? (nodes.Count == 1 || (nodes.Count == 2 && outer == 1 && nodes[0].TargetFramework != nodes[1].TargetFramework) ? null
                : outer > 0 ? "multi-targeted"
                : "built with several sets of properties");
            foreach (var node in nodes)
            {
                if (why is null)
                {
                    keyed.Add((node.Path, node.TargetFramework), node);
                }
                else
                {
                    unkeyed.Add((node, why));
                }
            }
        }

        // A scan sees everything under a project's directories, so another project's outputs there are not obsolete.
        // Sorted, every directory under another follows it, which spares comparing each pair of projects.
        // Not an outer node's: it writes nothing, and its directories are those of its inner nodes.
        var directories = projects
            .Where(p => !p.CrossTargeting)
            .SelectMany(p => p.OutputDirectories.Select(d => (Directory: d, Project: p)))
            .OrderBy(d => d.Directory, StringComparer.FromComparison(KeyComputer.PathComparison))
            .ToArray();
        sharing = [];
        for (var i = 0; i < directories.Length; i++)
        {
            var (own, project) = directories[i];
            for (var j = i + 1; j < directories.Length && directories[j].Directory.StartsWith(own, KeyComputer.PathComparison); j++)
            {
                if (directories[j].Project != project)
                {
                    sharing.Add(project);
                    if (directories[j].Directory.Length == own.Length)
                    {
                        sharing.Add(directories[j].Project);
                    }
                }
            }
        }
    }

    public IReadOnlyList<string> Environment { get; }

    /// <summary>
    /// What a look at a project's source tree skips, each with a separator at the end: every project's directory, bin/,
    /// obj/ and output directories. Another project inside one writes there in parallel, and outputs are no source.
    /// </summary>
    public HashSet<string> NotSource => field ??= Projects
        .SelectMany(p => p.OutputDirectories.Concat(p.OwnedRoots).Append(p.Directory))
        .ToHashSet(StringComparer.FromComparison(KeyComputer.PathComparison));

    /// <summary>Every node, each after the nodes it references.</summary>
    public IReadOnlyList<ProjectModel> Projects { get; }

    /// <summary>Each node that gets no key whatever its inputs, with the reason, worded to follow a count.</summary>
    public IReadOnlyList<(ProjectModel Project, string Why)> Unkeyed => unkeyed;

    /// <summary>The nodes of the given projects, both inner and outer, and every node they reference.</summary>
    public HashSet<ProjectModel> Closure(IEnumerable<ProjectModel> projects)
    {
        var paths = projects.Select(p => p.Path).ToHashSet(StringComparer.Ordinal);
        var closure = new HashSet<ProjectModel>();
        var pending = new Stack<ProjectModel>(Projects.Where(p => paths.Contains(p.Path)));
        while (pending.TryPop(out var project))
        {
            if (closure.Add(project))
            {
                foreach (var reference in project.References)
                {
                    pending.Push(reference);
                }
            }
        }

        return closure;
    }

    /// <summary>False for a path with no node or with several inner nodes, which then has no key.</summary>
    public bool TryGetProject(string path, string targetFramework, out ProjectModel project) => keyed.TryGetValue((path, targetFramework), out project!);

    public bool IsKeyed(ProjectModel project) => keyed.TryGetValue((project.Path, project.TargetFramework), out var only) && only == project;

    /// <summary>True when another project writes under one of this one's output directories.</summary>
    public bool Shares(ProjectModel project) => sharing.Contains(project);
}
