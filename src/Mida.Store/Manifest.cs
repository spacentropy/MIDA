using System.Text.Json.Serialization;

namespace Mida.Store;

/// <summary>What a key produced. Every path is relative to the repo root, and blobs are per file.</summary>
public sealed record Manifest(
    IReadOnlyList<ManifestFile> Files,
    IReadOnlyList<ManifestReference> References,
    IReadOnlyList<ManifestTarget> Targets);

/// <param name="Mode">Unix file mode, or null when written on Windows.</param>
public sealed record ManifestFile(string Path, string Hash, int? Mode);

/// <summary>
/// A copy of a dependency's file, taken from the dependency's current output on every hit. The key stops at the
/// dependency's reference assembly, so a stored copy could be stale. See docs/notes/project-cache-plugin.md
/// </summary>
public sealed record ManifestReference(string Path, string Source);

public sealed record ManifestTarget(string Name, IReadOnlyList<ManifestItem> Items);

public sealed record ManifestItem(string Spec, IReadOnlyDictionary<string, string> Metadata);

[JsonSerializable(typeof(Manifest))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true)]
internal sealed partial class ManifestJson : JsonSerializerContext;
