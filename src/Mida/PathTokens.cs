using System.Globalization;
using System.Text;

namespace Mida;

/// <summary>
/// Replaces this machine's directories with names that mean the same on every machine, and back. A stored graph and
/// the restore outputs kept beside it hold such paths. See docs/notes/stored-graph.md
/// </summary>
internal sealed class PathTokens
{
    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];

    // The longest first: the package folder is often inside the profile, and a repo can be.
    private readonly (string Path, string Token)[] roots;

    // For the repo, the profile and the package folder: the directories above it, nearest first. MSBuild, NuGet and
    // the SDK search upwards for files by name, from a project and from a source file a package brings.
    private readonly (string Prefix, string[] Directories)[] above;

    private readonly HashSet<string> rootPaths;

    public PathTokens(string repoRoot, string packageRoot, string dotnetRoot, string userProfile)
    {
        (string Path, string Name)[] named = [(repoRoot, "RepoRoot"), (packageRoot, "NuGetPackageRoot"), (dotnetRoot, "DotnetRoot"), (userProfile, "UserProfile")];
        var all = new List<(string Path, string Token)>();
        foreach (var (path, name) in named)
        {
            var trimmed = Path.TrimEndingDirectorySeparator(path);
            all.Add((trimmed, $"{{{name}}}"));
            // JSON doubles each backslash, so a Windows path has a second spelling there. Some writers escape each slash.
            all.Add(Path.DirectorySeparatorChar == '\\'
                ? (trimmed.Replace("\\", "\\\\", StringComparison.Ordinal), $"{{{name}:json}}")
                : (trimmed.Replace("/", "\\/", StringComparison.Ordinal), $"{{{name}:json}}"));
        }

        // A root of one character, such as /, is in every path.
        roots = [.. all.Where(r => r.Path.Length > 1).DistinctBy(r => r.Path, StringComparer.FromComparison(KeyComputer.PathComparison)).OrderByDescending(r => r.Path.Length)];
        rootPaths = named.Select(n => Path.TrimEndingDirectorySeparator(n.Path)).ToHashSet(StringComparer.FromComparison(KeyComputer.PathComparison));
        Plain = named.All(n => !n.Path.Any(c => char.IsControl(c) || c is '&' or '<' or '>' or '"' or '\'' or '%' or '$' or '@' or ';' or '?' or '*'
            || c == '\\' && Path.DirectorySeparatorChar != '\\'));
        RepoRoot = Path.TrimEndingDirectorySeparator(repoRoot);
        PackageRoot = Path.TrimEndingDirectorySeparator(packageRoot);
        above =
        [
            ("{Above:", Above(RepoRoot)),
            ("{AboveUserProfile:", Above(Path.TrimEndingDirectorySeparator(userProfile))),
            ("{AboveNuGetPackageRoot:", Above(PackageRoot)),
        ];
    }

    public string RepoRoot { get; }

    public string PackageRoot { get; }

    /// <summary>
    /// True when no root holds a character that XML, JSON or MSBuild would escape. Text that holds an escaped root does
    /// not show it where <see cref="Tokenize(string)"/> looks, and a root put back into text unescaped can break it.
    /// </summary>
    public bool Plain { get; }

    /// <summary>How many directories are above the repo, the profile and the package folder.</summary>
    public IReadOnlyList<int> Depths => [.. above.Select(a => a.Directories.Length)];

    /// <summary>Every root in a text, wherever a path starts with one.</summary>
    public string Tokenize(string text) => roots.Aggregate(text, (current, root) => Replace(current, root.Path, root.Token));

    public string Detokenize(string text) =>
        text.Contains('{', StringComparison.Ordinal) ? roots.Aggregate(text, (current, root) => current.Replace(root.Token, root.Path, StringComparison.Ordinal)) : text;

    /// <summary>
    /// One full path, the way a graph holds it. A file in a directory above the repo is named by how far above, because
    /// what reads it found it by searching upwards. A path that no name fits stays as it is, see <see cref="IsPlaced"/>.
    /// </summary>
    public string TokenizePath(string path) => Names(path).First();

    /// <summary>
    /// One full path, the way a question holds it: under every name that fits. A directory above the repo can be above
    /// the package folder too, and on another machine the two searches meet different files.
    /// </summary>
    public IEnumerable<string> TokenizeQuestion(string path) => Names(path);

    /// <summary>The path on this machine. One further above than this machine has directories names nothing that exists.</summary>
    public string DetokenizePath(string token)
    {
        if (!TryAbove(token, out var search, out var level, out var name))
        {
            return Detokenize(token);
        }

        var directories = above[search].Directories;
        return level <= directories.Length
            ? Path.Combine(directories[level - 1], name)
            : Path.Combine(directories.Length > 0 ? directories[^1] : RepoRoot, token[..(token.IndexOf('}', StringComparison.Ordinal) + 1)], name);
    }

    /// <summary>True for a path that holds no name: it means its place on the disk, so it only fits a checkout at the same place.</summary>
    public static bool IsPlaced(string token) => Path.IsPathRooted(token);

    /// <summary>
    /// For a token that places a file above a root: the same file name in each directory this machine has above that
    /// root beyond the <paramref name="depths"/> of the machine that stored the token. A search upwards goes on there.
    /// </summary>
    public IEnumerable<string> Higher(string token, IReadOnlyList<int> depths) =>
        TryAbove(token, out var search, out _, out var name) && search < depths.Count
            ? above[search].Directories.Skip(depths[search]).Select(directory => Path.Combine(directory, name))
            : [];

    /// <summary>Null for content that is not UTF-8 text, which then has no form for another machine.</summary>
    public byte[]? Tokenize(ReadOnlySpan<byte> content) => Rewrite(content, Tokenize);

    public byte[]? Detokenize(ReadOnlySpan<byte> content) => Rewrite(content, Detokenize);

    private static string[] Above(string root)
    {
        var directories = new List<string>();
        for (var directory = Path.GetDirectoryName(root); directory is not null; directory = Path.GetDirectoryName(directory))
        {
            directories.Add(directory);
        }

        return [.. directories];
    }

    // Above the repo first: the profile is often one of the directories above it, and a search upwards passes through.
    private IEnumerable<string> Names(string path)
    {
        // A root is named by its token alone. Named by how far above, it would keep this machine's name for it.
        if (rootPaths.Contains(Path.TrimEndingDirectorySeparator(path)))
        {
            return [Tokenize(path)];
        }

        var directory = Path.GetDirectoryName(path);
        string? AboveRoot(int search) =>
            Array.FindIndex(above[search].Directories, d => d.Equals(directory, KeyComputer.PathComparison)) is >= 0 and var level
                ? string.Create(CultureInfo.InvariantCulture, $"{above[search].Prefix}{level + 1}}}/{Path.GetFileName(path)}")
                : null;

        string?[] names = [AboveRoot(0), Tokenize(path) is var tokenized && tokenized != path ? tokenized : null, AboveRoot(1), AboveRoot(2)];
        return names.OfType<string>().DefaultIfEmpty(path);
    }

    private bool TryAbove(string token, out int search, out int level, out string name)
    {
        (level, name) = (0, string.Empty);
        // Not StartsWith('{'): CA1307 wants a StringComparison there on .NET 11, and .NET 10 has no such overload.
        search = token is ['{', ..] ?Array.FindIndex(above, a => token.StartsWith(a.Prefix, StringComparison.Ordinal)) : -1;
        if (search < 0)
        {
            return false;
        }

        var start = above[search].Prefix.Length;
        var end = token.IndexOf('}', StringComparison.Ordinal);
        if (end < start || end + 2 > token.Length || !int.TryParse(token.AsSpan(start, end - start), NumberStyles.None, CultureInfo.InvariantCulture, out level) || level < 1)
        {
            return false;
        }

        name = token[(end + 2)..];
        return true;
    }

    // NuGet writes its props and targets with a byte order mark, which the stored and the restored file keep.
    private static byte[]? Rewrite(ReadOnlySpan<byte> content, Func<string, string> rewrite)
    {
        var marked = content.StartsWith(Bom);
        var body = marked ? content[Bom.Length..] : content;
        string text;
        try
        {
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(body);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }

        var converted = Encoding.UTF8.GetBytes(rewrite(text));
        return marked ? [.. Bom, .. converted] : converted;
    }

    private static string Replace(string text, string path, string token)
    {
        var index = text.IndexOf(path, KeyComputer.PathComparison);
        if (index < 0)
        {
            return text;
        }

        var result = new StringBuilder(text.Length);
        var copied = 0;
        for (; index >= 0; index = text.IndexOf(path, index + path.Length, KeyComputer.PathComparison))
        {
            var end = index + path.Length;
            if (StartsPath(text, index) && EndsName(text, end))
            {
                result.Append(text, copied, index - copied).Append(token);
                copied = end;
            }
        }

        return result.Append(text, copied, text.Length - copied).ToString();
    }

    // /repo is a root neither in /x/repo/a nor in /repo2/a.
    private static bool StartsPath(string text, int index) =>
        index == 0 || !(char.IsLetterOrDigit(text[index - 1]) || text[index - 1] is '/' or '\\' or '_' or '-' or '.' or '~');

    private static bool EndsName(string text, int end) =>
        end == text.Length || text[end] is '/' or '\\' or '"' or '<' or ';' or '\r' or '\n';
}
