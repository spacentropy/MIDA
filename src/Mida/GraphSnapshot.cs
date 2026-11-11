using System.Text.Json.Serialization;

namespace Mida;

/// <summary>
/// What mida needs of an evaluated graph, in a form another checkout can read: every path that names the repo or the
/// package folder holds a token from <see cref="PathTokens"/>. It stands for the evaluation only while each of its
/// <see cref="Questions"/> still has its answer. See docs/notes/stored-graph.md
/// </summary>
/// <param name="Environment">The key lines that name the SDK, MSBuild and the platform.</param>
/// <param name="Projects">One per graph node, each after the nodes it references.</param>
internal sealed record GraphSnapshot(
    IReadOnlyList<string> Environment,
    IReadOnlyList<SnapshotProject> Projects,
    SnapshotQuestions Questions,
    SnapshotRestore Restore);

/// <param name="Properties">The global properties a key holds.</param>
/// <param name="Inputs">The predicted input files a key reads, so none of the SDK's, the packages' or the outputs'.</param>
/// <param name="References">Indexes into the projects.</param>
/// <param name="BuildTimeReferences">The referenced project files whose behaviour shapes this project's output, such as a generator's.</param>
/// <param name="OwnedRoots">bin/ and obj/, where a hit may delete.</param>
/// <param name="OwnCopies">Where the project's own items land in its output directory.</param>
/// <param name="ExtensionsPath">Where restore writes, obj/ unless the project says otherwise.</param>
internal sealed record SnapshotProject(
    string Path,
    IReadOnlyList<string> Targets,
    IReadOnlyDictionary<string, string> Properties,
    IReadOnlyList<string> Inputs,
    string? AssetsFile,
    IReadOnlyList<int> References,
    IReadOnlyList<string> BuildTimeReferences,
    bool CrossTargeting,
    string? TargetRefPath,
    string? TargetPath,
    string OutDir,
    IReadOnlyList<string> OutputDirectories,
    IReadOnlyList<string> OwnedRoots,
    IReadOnlyList<string> OwnCopies,
    string? PackageRoot,
    string? ExtensionsPath);

/// <param name="Files">A file whose content the evaluation depends on, with its hash, or null where there was none.</param>
/// <param name="Environment">A variable the evaluation read, with its value.</param>
/// <param name="Depths">
/// How many directories were above the repo, the profile and the package folder. A machine with more has more places
/// where a search upwards can find something.
/// </param>
/// <param name="Root">The repo root, when a path in the graph or a question names a place outside every token. Then only a checkout there fits.</param>
internal sealed record SnapshotQuestions(
    IReadOnlyList<FileQuestion> Files,
    IReadOnlyList<ExistsQuestion> Exists,
    IReadOnlyList<ListQuestion> Lists,
    IReadOnlyDictionary<string, string?> Environment,
    IReadOnlyList<int> Depths,
    string? Root);

internal sealed record FileQuestion(string Path, string? Hash);

/// <param name="Kind">f for a file, d for a directory, e for either.</param>
internal sealed record ExistsQuestion(char Kind, string Path, bool Answer);

/// <param name="Kind">f for files, d for directories, e for both.</param>
/// <param name="Answer">A hash of the names found.</param>
internal sealed record ListQuestion(char Kind, string Path, string Pattern, bool Recursive, string Answer);

/// <param name="Reusable">False when a restore could resolve differently with no file changed, as with a floating version.</param>
/// <param name="Packages">The files NuGet expects in the package folder. Without them a restore has to run.</param>
/// <param name="Files">The assets file and the generated props and targets of each project.</param>
internal sealed record SnapshotRestore(bool Reusable, IReadOnlyList<string> Packages, IReadOnlyList<RestoreFile> Files)
{
    public static SnapshotRestore None { get; } = new(false, [], []);
}

/// <param name="Path">Relative to the repo root.</param>
/// <param name="Hash">The blob, which holds the file with its paths as tokens.</param>
internal sealed record RestoreFile(string Path, string Hash);

[JsonSerializable(typeof(GraphSnapshot))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true)]
internal sealed partial class GraphJson : JsonSerializerContext;
