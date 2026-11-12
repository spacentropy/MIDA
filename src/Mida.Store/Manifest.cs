using System.Text.Json.Serialization;

namespace Mida.Store;

/// <summary>What a key produced. Every path is relative to the repo root, and blobs are per file.</summary>
/// <param name="BuildTime">How long the build that stored it took, which a hit reports as saved.</param>
public sealed record Manifest(
    IReadOnlyList<ManifestFile> Files,
    IReadOnlyList<ManifestReference> References,
    IReadOnlyList<ManifestTarget> Targets,
    TimeSpan BuildTime = default);

/// <param name="Mode">Unix file mode, or null when written on Windows.</param>
/// <param name="Tokenized">The blob holds tokens for this machine's paths, which a restore puts back. See <see cref="Relocation"/></param>
public sealed record ManifestFile(
    string Path,
    string Hash,
    int? Mode,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Tokenized = false);

/// <summary>
/// Puts tokens for this machine's paths into a file and back. <paramref name="Store"/> gets the path relative to the
/// repo and the bytes, returns null for a file it leaves alone, and throws for one that must not be stored.
/// See docs/notes/static-web-assets.md
/// </summary>
public sealed record Relocation(Func<string, byte[], byte[]?> Store, Func<byte[], byte[]> Restore);

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
