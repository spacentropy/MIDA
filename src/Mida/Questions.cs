using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Mida.Store;

namespace Mida;

/// <summary>
/// What an evaluation asked of the machine: file contents, what exists, what a directory lists and environment
/// variables. Asked again in another run, the same answers mean the same evaluation. See docs/notes/stored-graph.md
/// </summary>
/// <param name="variables">
/// The environment as the process found it. MSBuild sets variables for itself once it runs, and a later run that needs
/// no MSBuild has to see what this one saw.
/// </param>
internal sealed class Questions(string dotnetRoot, ConcurrentDictionary<string, string> hashes, IReadOnlyDictionary<string, string> variables)
{
    public const char Files = 'f';
    public const char Directories = 'd';
    public const char Either = 'e';

    private const string Missing = "missing";

    private readonly string dotnetPrefix = KeyComputer.WithSeparator(dotnetRoot);
    private readonly ConcurrentDictionary<string, string?> files = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(char Kind, string Path), bool> exists = new();
    private readonly ConcurrentDictionary<(char Kind, string Path, string Pattern, bool Recursive), string> lists = new();
    private readonly ConcurrentDictionary<string, string?> environment = new(StringComparer.OrdinalIgnoreCase);
    private string? unanswerable;

    /// <summary>Set when an answer cannot be had again the way the evaluation got it, so nothing may be stored.</summary>
    public string? Unanswerable => Volatile.Read(ref unanswerable);

    public void AddFile(string path)
    {
        if (Normalize(path) is { } full)
        {
            files.GetOrAdd(full, p => HashOrNull(p, hashes));
        }
    }

    public void AddExists(char kind, string path)
    {
        if (Normalize(path) is { } full)
        {
            exists.GetOrAdd((kind, full), q => AnswerExists(q.Kind, q.Path));
        }
    }

    /// <summary>For an answer MSBuild got itself, which has to be the one a later run will get.</summary>
    public void AddExists(char kind, string path, bool answer)
    {
        if (Normalize(path) is { } full && exists.GetOrAdd((kind, full), q => AnswerExists(q.Kind, q.Path)) != answer)
        {
            Interlocked.CompareExchange(ref unanswerable, $"whether {path} exists has two answers", null);
        }
    }

    public void AddList(char kind, string path, string pattern, bool recursive)
    {
        if (Normalize(path) is { } full)
        {
            lists.GetOrAdd((kind, full, pattern, recursive), q => AnswerList(q.Kind, q.Path, q.Pattern, q.Recursive));
        }
    }

    public void AddEnvironment(string name) => environment.GetOrAdd(name, variables.GetValueOrDefault);

    /// <summary>For a property an evaluation read: while no variable has its name, that it has none is the answer.</summary>
    public void AddUnset(string name)
    {
        if (!variables.ContainsKey(name))
        {
            environment.TryAdd(name, null);
        }
    }

    /// <param name="placed">True when the graph itself holds a path that names a place on this disk.</param>
    public SnapshotQuestions Store(PathTokens tokens, bool placed)
    {
        FileQuestion[] storedFiles =
        [
            .. files.SelectMany(f => tokens.TokenizeQuestion(f.Key).Select(path => new FileQuestion(path, f.Value))).OrderBy(f => f.Path, StringComparer.Ordinal),
        ];
        ExistsQuestion[] storedExists =
        [
            .. exists.SelectMany(e => tokens.TokenizeQuestion(e.Key.Path).Select(path => new ExistsQuestion(e.Key.Kind, path, e.Value)))
                .OrderBy(e => e.Path, StringComparer.Ordinal).ThenBy(e => e.Kind),
        ];
        ListQuestion[] storedLists =
        [
            .. lists.SelectMany(l => tokens.TokenizeQuestion(l.Key.Path).Select(path => new ListQuestion(l.Key.Kind, path, l.Key.Pattern, l.Key.Recursive, l.Value)))
                .OrderBy(l => l.Path, StringComparer.Ordinal).ThenBy(l => l.Kind).ThenBy(l => l.Pattern, StringComparer.Ordinal).ThenBy(l => l.Recursive),
        ];
        placed |= storedFiles.Any(f => PathTokens.IsPlaced(f.Path)) || storedExists.Any(e => PathTokens.IsPlaced(e.Path)) || storedLists.Any(l => PathTokens.IsPlaced(l.Path));
        return new SnapshotQuestions(
            storedFiles,
            storedExists,
            storedLists,
            // A value can name a root, such as the SDK's MSBuildExtensionsPath, and then means the same on another machine.
            new SortedDictionary<string, string?>(environment.ToDictionary(e => e.Key, e => Value(e.Value, tokens)), StringComparer.Ordinal),
            tokens.Depths,
            placed ? tokens.RepoRoot : null);
    }

    /// <summary>The first content or variable that changed, or null. These do not depend on what a restore wrote.</summary>
    public static string? ChangedSource(
        SnapshotQuestions stored, PathTokens tokens, ConcurrentDictionary<string, string> hashes, IReadOnlyDictionary<string, string> variables)
    {
        if (stored.Root is { } root && !root.Equals(tokens.RepoRoot, KeyComputer.PathComparison))
        {
            return "the path of the checkout";
        }

        if (stored.Environment.FirstOrDefault(e => Value(variables.GetValueOrDefault(e.Key), tokens) != e.Value) is { Key: not null } variable)
        {
            return $"the environment variable {variable.Key}";
        }

        // A search upwards that found nothing above the stored checkout goes on where this one has more above it.
        return First(stored.Files, f => HashOrNull(tokens.DetokenizePath(f.Path), hashes) != f.Hash)?.Path
            ?? Higher(stored.Files.Select(f => f.Path), stored.Depths, tokens).FirstOrDefault(File.Exists);
    }

    /// <summary>The first path that exists or lists differently, or null. Asked once the restore outputs are in place.</summary>
    public static string? ChangedProbe(SnapshotQuestions stored, PathTokens tokens) =>
        First(stored.Exists, e => AnswerExists(e.Kind, tokens.DetokenizePath(e.Path)) != e.Answer)?.Path
        ?? First(stored.Lists, l => AnswerList(l.Kind, tokens.DetokenizePath(l.Path), l.Pattern, l.Recursive) != l.Answer)?.Path
        ?? Higher(stored.Exists.Select(e => e.Path), stored.Depths, tokens).FirstOrDefault(Path.Exists);

    /// <summary>A hash of this checkout's answers, which tells apart the graphs stored for one solution.</summary>
    public static string Answers(
        SnapshotQuestions stored, PathTokens tokens, ConcurrentDictionary<string, string> hashes, IReadOnlyDictionary<string, string> variables)
    {
        var files = Each(stored.Files, f => HashOrNull(tokens.DetokenizePath(f.Path), hashes) ?? Missing);
        var exists = Each(stored.Exists, e => AnswerExists(e.Kind, tokens.DetokenizePath(e.Path)) ? "1" : "0");
        var lists = Each(stored.Lists, l => AnswerList(l.Kind, tokens.DetokenizePath(l.Path), l.Pattern, l.Recursive));
        var values = stored.Environment.Select(e => Value(variables.GetValueOrDefault(e.Key), tokens) ?? Missing);
        var place = stored.Root is null ? string.Join(' ', tokens.Depths.Select(d => d.ToString(CultureInfo.InvariantCulture))) : tokens.RepoRoot;
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', [.. files, .. exists, .. lists, .. values, place]))));
    }

    // The names asked for above a root of the stored checkout, in each directory this machine has above that root beyond it.
    private static IEnumerable<string> Higher(IEnumerable<string> paths, IReadOnlyList<int> depths, PathTokens tokens) =>
        paths.SelectMany(path => tokens.Higher(path, depths)).Distinct(StringComparer.Ordinal);

    private static string? Value(string? value, PathTokens tokens) => value is null ? null : tokens.Tokenize(value);

    private static string[] Each<T>(IReadOnlyList<T> questions, Func<T, string> answer)
    {
        var answers = new string[questions.Count];
        Parallel.For(0, questions.Count, i => answers[i] = answer(questions[i]));
        return answers;
    }

    // The lowest index, so the same question is named in every run.
    private static T? First<T>(IReadOnlyList<T> questions, Func<T, bool> changed)
        where T : class
    {
        var first = questions.Count;
        Parallel.For(0, questions.Count, i =>
        {
            if (i < Volatile.Read(ref first) && changed(questions[i]))
            {
                int seen;
                while (i < (seen = Volatile.Read(ref first)) && Interlocked.CompareExchange(ref first, i, seen) != seen)
                {
                }
            }
        });
        return first < questions.Count ? questions[first] : null;
    }

    private static string? HashOrNull(string path, ConcurrentDictionary<string, string> hashes)
    {
        try
        {
            return File.Exists(path) ? hashes.GetOrAdd(path, OutputCache.Hash) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool AnswerExists(char kind, string path) => kind switch
    {
        Files => File.Exists(path),
        Directories => Directory.Exists(path),
        _ => Path.Exists(path),
    };

    private static string AnswerList(char kind, string path, string pattern, bool recursive)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                return Missing;
            }

            var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var entries = kind switch
            {
                Files => Directory.EnumerateFiles(path, pattern, option),
                Directories => Directory.EnumerateDirectories(path, pattern, option),
                _ => Directory.EnumerateFileSystemEntries(path, pattern, option),
            };
            var names = entries
                .Select(e => Path.GetRelativePath(path, e))
                .Where(n => !IsBookkeeping(Path.GetFileName(n)))
                .Order(StringComparer.Ordinal);
            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', names))))[..32];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Missing;
        }
    }

    /// <summary>
    /// What NuGet keeps beside its outputs for its own next restore, and mida's note of what a hit restored. No project
    /// imports them, and a stored graph does not bring them back, so a listing of obj/ counts without them.
    /// </summary>
    private static bool IsBookkeeping(string fileName) =>
        fileName == "project.nuget.cache"
        || fileName.EndsWith(".nuget.dgspec.json", StringComparison.Ordinal)
        || fileName.EndsWith(".mida-restored.txt", StringComparison.Ordinal);

    // The SDK's own files are covered by its version, like its inputs in a key.
    private string? Normalize(string path)
    {
        if (path.Length == 0)
        {
            return null;
        }

        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return full.StartsWith(dotnetPrefix, KeyComputer.PathComparison) ? null : full;
    }
}
