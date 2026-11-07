namespace Mida.Store;

internal static class StorePath
{
    /// <summary>Keys and manifest paths come from the store, which is not trusted to stay inside <paramref name="root"/>.</summary>
    public static string Contained(string root, string relative)
    {
        if (relative.Contains('\0', StringComparison.Ordinal))
        {
            throw new InvalidDataException($"A path under {root} holds a NUL");
        }

        var fullRoot = Path.GetFullPath(root);
        var full = Path.GetFullPath(relative, fullRoot);
        return full.StartsWith(Path.EndsInDirectorySeparator(fullRoot) ? fullRoot : fullRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? full
            : throw new InvalidDataException($"{relative} is outside {fullRoot}");
    }
}
