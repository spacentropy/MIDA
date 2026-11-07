using Microsoft.Build.Execution;
using Microsoft.Build.Prediction;

namespace Mida;

internal static class ProjectOutputs
{
    public static IEnumerable<string> Directories(ProjectInstance project, ProjectPredictions predictions) =>
        predictions.OutputDirectories
            .Select(d => KeyComputer.AbsolutePath(project, d.Path))
            .OfType<string>()
            .Select(KeyComputer.WithSeparator);

    /// <summary>What a hit needs of a finished project. Run after it has finished.</summary>
    public static List<string> Scan(ProjectInstance project, ProjectPredictions predictions) =>
    [
        .. Directories(project, predictions)
            .Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories))
            .Where(f => IsCached(Path.GetFileName(f)))
            .Distinct(StringComparer.Ordinal),
    ];

    /// <summary>
    /// Static web asset manifests name their content roots absolutely, and an app reads them at startup, so a relocated
    /// one crashes it once the writer's checkout is gone. See docs/notes/local-store.md
    /// </summary>
    // shortcut: such a project is never stored, so it always misses. Upgrade by giving these manifests a {RepoRoot} token.
    public static bool HoldCheckoutPaths(IEnumerable<string> outputs, string repoRoot)
    {
        var escapedRoot = repoRoot.Replace("\\", "\\\\", StringComparison.Ordinal);
        return outputs
            .Where(f => Path.GetFileName(f).Contains("staticwebassets", StringComparison.Ordinal) && Path.GetExtension(f) == ".json")
            .Any(f => File.ReadAllText(f).Contains(escapedRoot, StringComparison.Ordinal));
    }

    /// <summary>Restore owns some files, and the rest hold absolute paths only incremental builds read. See docs/notes/relocation.md</summary>
    public static bool IsCached(string fileName) => !(fileName == "project.assets.json"
        || fileName == "project.nuget.cache"
        || fileName.EndsWith(".nuget.dgspec.json", StringComparison.Ordinal)
        || fileName.EndsWith(".nuget.g.props", StringComparison.Ordinal)
        || fileName.EndsWith(".nuget.g.targets", StringComparison.Ordinal)
        || fileName.EndsWith(".assets.cache", StringComparison.Ordinal)
        || fileName.EndsWith("AssemblyReference.cache", StringComparison.Ordinal)
        || fileName.EndsWith(".CoreCompileInputs.cache", StringComparison.Ordinal)
        || fileName.EndsWith(".GeneratedMSBuildEditorConfig.editorconfig", StringComparison.Ordinal)
        || fileName.EndsWith(".FileListAbsolute.txt", StringComparison.Ordinal));
}
