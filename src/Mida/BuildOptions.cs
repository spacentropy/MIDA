namespace Mida;

internal sealed record BuildOptions(string Solution, bool PrintKeys, string? KeyDetailsDirectory)
{
    public static BuildOptions? Parse(IReadOnlyList<string> args)
    {
        string? solution = null;
        string? details = null;
        var printKeys = false;

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
                case var path when solution is null && path is not ['-', ..]:
                    solution = path;
                    break;
                default:
                    return null;
            }
        }

        solution ??= FindSolution();
        return solution is null ? null : new BuildOptions(Path.GetFullPath(solution), printKeys, details);
    }

    private static string? FindSolution()
    {
        var found = Directory.EnumerateFiles(Directory.GetCurrentDirectory(), "*.sln*")
            .Where(f => Path.GetExtension(f) is ".sln" or ".slnx")
            .ToList();
        return found.Count == 1 ? found[0] : null;
    }
}
