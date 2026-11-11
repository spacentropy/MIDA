namespace Mida;

internal static class ProjectOutputs
{
    /// <summary>What a hit needs of a finished project. Run after it has finished.</summary>
    // Nothing for an outer node: the files in its directories are its inner nodes', and a miss would clear them away.
    public static List<string> Scan(ProjectModel project)
    {
        if (project.CrossTargeting)
        {
            return [];
        }

        // Outside bin/ and obj/, an output directory can hold the repo's own files, so only what the build wrote is an output.
        var written = Written(project);
        return
        [
            .. project.OutputDirectories
                .Where(Directory.Exists)
                .SelectMany(d => Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories))
                .Where(f => IsCached(Path.GetFileName(f)) && (project.Owns(f) || written.Contains(f)))
                .Distinct(StringComparer.Ordinal),
        ];
    }

    /// <summary>The files the project's last build says it wrote, in the list MSBuild's incremental clean reads.</summary>
    public static HashSet<string> Written(ProjectModel project)
    {
        var written = new HashSet<string>(StringComparer.FromComparison(KeyComputer.PathComparison));
        var directory = Path.GetDirectoryName(project.Path)!;
        var name = $"{Path.GetFileName(project.Path)}.FileListAbsolute.txt";
        foreach (var list in project.OutputDirectories.Select(d => Path.Combine(d, name)).Where(File.Exists))
        {
            written.UnionWith(File.ReadLines(list).Where(line => line.Length > 0).Select(line => Path.GetFullPath(line, directory)));
        }

        return written;
    }

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
    public static bool IsCached(string fileName) => !(IsRestored(fileName)
        || fileName == "project.nuget.cache"
        || fileName.EndsWith(".nuget.dgspec.json", StringComparison.Ordinal)
        || fileName.EndsWith(".assets.cache", StringComparison.Ordinal)
        || fileName.EndsWith("AssemblyReference.cache", StringComparison.Ordinal)
        || fileName.EndsWith(".CoreCompileInputs.cache", StringComparison.Ordinal)
        || fileName.EndsWith(".GeneratedMSBuildEditorConfig.editorconfig", StringComparison.Ordinal)
        || fileName.EndsWith(".FileListAbsolute.txt", StringComparison.Ordinal)
        || fileName.EndsWith(".mida-restored.txt", StringComparison.Ordinal));

    /// <summary>The restore outputs a build reads, which a stored graph brings back. See docs/notes/stored-graph.md</summary>
    public static bool IsRestored(string fileName) => fileName == "project.assets.json"
        || fileName.EndsWith(".nuget.g.props", StringComparison.Ordinal)
        || fileName.EndsWith(".nuget.g.targets", StringComparison.Ordinal);
}
