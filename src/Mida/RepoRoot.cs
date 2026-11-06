namespace Mida;

internal static class RepoRoot
{
    public static string Find(string solution)
    {
        var start = Path.GetDirectoryName(solution)!;
        for (var directory = start; directory is not null; directory = Path.GetDirectoryName(directory))
        {
            // A file, not a directory, in a worktree.
            if (Path.Exists(Path.Combine(directory, ".git")))
            {
                return directory;
            }
        }

        return start;
    }
}
