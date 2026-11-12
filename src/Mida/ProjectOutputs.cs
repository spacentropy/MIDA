using System.Text.Json;
using Mida.Store;

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
    /// Static web asset manifests name their content roots absolutely, and an app reads them at startup, so the store
    /// holds them with tokens for the roots. See docs/notes/static-web-assets.md
    /// </summary>
    public static Relocation Relocation(PathTokens tokens) => new(
        (path, content) => Tokenize(path, content, tokens),
        // A root that JSON would escape cannot go back in as it is.
        content => tokens.Plain ? tokens.Detokenize(content)! : throw new InvalidDataException("this checkout's paths hold characters JSON escapes"));

    /// <summary>
    /// Chosen by name, never by content: a PDB holds the root too, and a binary does not survive a text replace.
    /// WebApplicationFactory takes the host's content root from MvcTestingAppManifest.json, without checking it exists.
    /// </summary>
    public static bool IsTokenized(string fileName) => fileName.EndsWith(".staticwebassets.runtime.json", StringComparison.Ordinal)
        || fileName is "staticwebassets.build.json" or "staticwebassets.development.json" or "staticwebassets.pack.json" or "MvcTestingAppManifest.json";

    // Every static web asset manifest is checked, so one that names a path no token covers is never stored.
    private static byte[]? Tokenize(string path, byte[] content, PathTokens tokens)
    {
        var name = Path.GetFileName(path);
        if (!IsTokenized(name) && !(name.Contains("staticwebassets", StringComparison.Ordinal) && Path.GetExtension(name) == ".json"))
        {
            return null;
        }

        var tokenized = IsTokenized(name) ? tokens.Tokenize(content) ?? throw new InvalidDataException($"{path} is not UTF-8") : content;
        if (FullPath(tokenized) is { } full)
        {
            throw new InvalidDataException($"{path} names {full}, which no token covers");
        }

        return IsTokenized(name) ? tokenized : null;
    }

    // What these manifests name exists once the build wrote them, and a hash in base64 that starts with a slash does not.
    // "/" alone is a base path.
    private static string? FullPath(byte[] json)
    {
        ReadOnlySpan<byte> bom = [0xEF, 0xBB, 0xBF];
        var reader = new Utf8JsonReader(json.AsSpan().StartsWith(bom) ? json.AsSpan(bom.Length) : json);
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.String && reader.GetString() is { Length: > 1 } value && Path.IsPathFullyQualified(value) && Path.Exists(value))
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>
    /// Restore owns some files, and the rest hold absolute paths only incremental builds read, or a key pair that a build
    /// makes and dotnet watch reads. See docs/notes/relocation.md and docs/notes/static-web-assets.md
    /// </summary>
    public static bool IsCached(string fileName) => !(IsRestored(fileName)
        || fileName == "project.nuget.cache"
        || fileName.EndsWith(".dswa.cache.json", StringComparison.Ordinal)
        || fileName is "staticwebassets.build.json.cache" or "swae.build.ex.cache"
        || fileName is "browser-tools-key.private.json" or "browser-tools-key.public.json"
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
