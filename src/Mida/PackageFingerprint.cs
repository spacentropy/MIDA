using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Mida;

internal static class PackageFingerprint
{
    // The sections that say what was resolved. The others, "project" and "packageFolders", hold absolute paths.
    private static readonly byte[][] Sections = [.. new[] { "version", "targets", "libraries", "projectFileDependencyGroups" }.Select(Encoding.UTF8.GetBytes)];

    public static string Compute(ReadOnlySpan<byte> assetsFile)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        // Streamed, not parsed: these files reach hundreds of KB per project, and allocating a document each costs more than all the source hashing.
        var reader = new Utf8JsonReader(assetsFile);
        reader.Read();
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var wanted = false;
            foreach (var section in Sections)
            {
                wanted |= reader.ValueTextEquals(section);
            }

            reader.Read();
            var start = (int)reader.TokenStartIndex;
            reader.Skip();
            if (wanted)
            {
                hash.AppendData(assetsFile[start..(int)reader.BytesConsumed]);
                hash.AppendData("\n"u8);
            }
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
