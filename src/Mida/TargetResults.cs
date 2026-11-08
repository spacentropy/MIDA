using Microsoft.Build.Evaluation;
using Microsoft.Build.Execution;
using Microsoft.Build.Framework;
using Microsoft.Build.ProjectCache;
using Microsoft.Build.Utilities;
using Mida.Store;

namespace Mida;

/// <summary>
/// Replayed on a hit so no target runs. Items hold absolute paths, which <c>{RepoRoot}</c> makes relocatable, and
/// <c>{NuGetPackageRoot}</c> for another machine's package folder: <c>InitializeSourceRootMappedPaths</c> returns it.
/// </summary>
internal sealed class TargetResults(string repoRoot)
{
    private readonly (string Path, string Token) repo = (repoRoot + Path.DirectorySeparatorChar, "{RepoRoot}/");

    public List<ManifestTarget> Capture(BuildResult result, IEnumerable<string> targets, ProjectInstance project)
    {
        var roots = Roots(project);
        return [.. targets.Order(StringComparer.Ordinal).Select(target => new ManifestTarget(target, [.. result[target].Items.Select(i => Capture(i, roots))]))];
    }

    public List<PluginTargetResult> Replay(IEnumerable<ManifestTarget> targets, ProjectInstance project)
    {
        var roots = Roots(project);
        return [.. targets.Select(target => new PluginTargetResult(target.Name, [.. target.Items.Select(i => Replay(i, roots))], BuildResultCode.Success))];
    }

    private static ManifestItem Capture(ITaskItem item, (string Path, string Token)[] roots)
    {
        var metadata = item.CloneCustomMetadata();
        var values = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (string name in metadata.Keys)
        {
            values[name] = Tokenize((string)metadata[name]!, roots);
        }

        return new ManifestItem(Tokenize(item.ItemSpec, roots), values);
    }

    // Read unescaped above, while TaskItem takes the escaped form.
    private static TaskItem Replay(ManifestItem stored, (string Path, string Token)[] roots)
    {
        var item = new TaskItem(Detokenize(stored.Spec, roots));
        foreach (var (name, value) in stored.Metadata)
        {
            item.SetMetadata(name, Detokenize(value, roots));
        }

        return item;
    }

    private static string Tokenize(string value, (string Path, string Token)[] roots) =>
        roots.Aggregate(value, (current, root) => current.Replace(root.Path, root.Token, KeyComputer.PathComparison));

    private static string Detokenize(string value, (string Path, string Token)[] roots) =>
        ProjectCollection.Escape(roots.Aggregate(value, (current, root) => current.Replace(root.Token, root.Path, StringComparison.Ordinal)));

    private (string Path, string Token)[] Roots(ProjectInstance project) =>
        project.GetPropertyValue("NuGetPackageRoot") is { Length: > 0 } packages
            ? [repo, (KeyComputer.WithSeparator(packages), "{NuGetPackageRoot}/")]
            : [repo];
}
