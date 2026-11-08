using Microsoft.Build.Evaluation;
using Microsoft.Build.Execution;
using Microsoft.Build.Framework;
using Microsoft.Build.ProjectCache;
using Microsoft.Build.Utilities;
using Mida.Store;

namespace Mida;

/// <summary>Replayed on a hit so no target runs. Items hold absolute paths, which <c>{RepoRoot}</c> makes relocatable.</summary>
internal sealed class TargetResults(string repoRoot)
{
    private const string Token = "{RepoRoot}/";

    private readonly string prefix = repoRoot + Path.DirectorySeparatorChar;

    public List<ManifestTarget> Capture(BuildResult result, IEnumerable<string> targets) =>
        [.. targets.Order(StringComparer.Ordinal).Select(target => new ManifestTarget(target, [.. result[target].Items.Select(Capture)]))];

    public List<PluginTargetResult> Replay(IEnumerable<ManifestTarget> targets) =>
        [.. targets.Select(target => new PluginTargetResult(target.Name, [.. target.Items.Select(Replay)], BuildResultCode.Success))];

    private ManifestItem Capture(ITaskItem item)
    {
        var metadata = item.CloneCustomMetadata();
        var values = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (string name in metadata.Keys)
        {
            values[name] = Tokenize((string)metadata[name]!);
        }

        return new ManifestItem(Tokenize(item.ItemSpec), values);
    }

    // Read unescaped above, while TaskItem takes the escaped form.
    private TaskItem Replay(ManifestItem stored)
    {
        var item = new TaskItem(Detokenize(stored.Spec));
        foreach (var (name, value) in stored.Metadata)
        {
            item.SetMetadata(name, Detokenize(value));
        }

        return item;
    }

    private string Tokenize(string value) => value.Replace(prefix, Token, KeyComputer.PathComparison);

    private string Detokenize(string value) => ProjectCollection.Escape(value.Replace(Token, prefix, StringComparison.Ordinal));
}
