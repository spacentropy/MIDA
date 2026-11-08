namespace Mida;

internal sealed record BuildOptions(
    string Solution,
    bool PrintKeys,
    string? KeyDetailsDirectory,
    string CacheDirectory,
    IReadOnlyDictionary<string, string> Properties)
{
    private static string DefaultCacheDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".mida", "cache");

    public static BuildOptions? Parse(IReadOnlyList<string> args)
    {
        string? solution = null;
        string? details = null;
        string? cache = null;
        var printKeys = false;
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--print-keys":
                    printKeys = true;
                    break;
                case "--key-details" when i + 1 < args.Count:
                    details = Path.GetFullPath(args[++i]);
                    break;
                case "--cache" when i + 1 < args.Count:
                    cache = Path.GetFullPath(args[++i]);
                    break;
                case "-c" or "--configuration" when i + 1 < args.Count:
                    properties["Configuration"] = args[++i];
                    break;
                // One property per -p, unlike dotnet build: splitting on ';' would break values such as DefineConstants.
                case ['-', 'p', ':', .. var property] when property.IndexOf('=', StringComparison.Ordinal) is > 0 and var equals:
                    properties[property[..equals]] = property[(equals + 1)..];
                    break;
                case var path when solution is null && path is not ['-', ..]:
                    solution = path;
                    break;
                default:
                    return null;
            }
        }

        solution ??= FindSolution();
        return solution is null ? null : new BuildOptions(Path.GetFullPath(solution), printKeys, details, cache ?? DefaultCacheDirectory, properties);
    }

    private static string? FindSolution()
    {
        var found = Directory.EnumerateFiles(Directory.GetCurrentDirectory(), "*.sln*")
            .Where(f => Path.GetExtension(f) is ".sln" or ".slnx")
            .ToList();
        return found.Count == 1 ? found[0] : null;
    }
}
