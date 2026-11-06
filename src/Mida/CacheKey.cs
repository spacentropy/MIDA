using System.Security.Cryptography;
using System.Text;

namespace Mida;

internal sealed record CacheKey(string Value, IReadOnlyList<string> Lines)
{
    public static CacheKey From(IEnumerable<string> lines)
    {
        // Sorted, so the order predictors report files in cannot change the key.
        var sorted = lines.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var text = string.Join('\n', sorted);
        return new CacheKey(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text))), sorted);
    }
}
