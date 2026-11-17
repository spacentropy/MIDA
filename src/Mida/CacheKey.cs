using System.Security.Cryptography;
using System.Text;

namespace Mida;

internal sealed record CacheKey(string Value, IReadOnlyList<string> Lines)
{
    public static CacheKey From(IEnumerable<string> lines)
    {
        // Sorted, so the order predictors report files in cannot change the key.
        var sorted = lines.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        // Each line after its length: a line holding a newline, such as a runner argument or a -p value, cannot then pass for two.
        var text = string.Concat(sorted.Select(line => $"{line.Length} {line}\n"));
        return new CacheKey(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text))), sorted);
    }
}
