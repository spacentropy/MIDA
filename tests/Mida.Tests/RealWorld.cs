using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using static Mida.Tests.TestRepo;

namespace Mida.Tests;

/// <summary>
/// A real repo at a pinned tag, built by plain dotnet build and by mida build: both must leave the same files under
/// bin, and the times are recorded. See docs/notes/benchmarks.md. Never part of the suite:
/// dotnet test --project tests/Mida.Tests -f net11.0 --treenode-filter "/*/*/RealWorld/OrchardCore" --output Detailed
/// .github/workflows/realworld.yml runs them on the packed tool.
/// </summary>
[Explicit]
[NotInParallel]
public sealed class RealWorld : IDisposable
{
    // What mida build injects, so that a plain build's outputs do not name its checkout either.
    private static readonly string[] Relocatable = ["-p:ContinuousIntegrationBuild=true", "-p:IncludeSourceRevisionInInformationalVersion=false"];

    private readonly string temp = Directory.CreateTempSubdirectory("mida-realworld-").FullName;

    public void Dispose() => Directory.Delete(temp, recursive: true);

    /// <summary>A runnable app: a CMS of some 230 projects, most of them Razor modules.</summary>
    [Test]
    public Task OrchardCore() => Compare("https://github.com/OrchardCMS/OrchardCore", "v3.0.1", "OrchardCore.slnx");

    /// <summary>A library: some 270 projects of the framework alone.</summary>
    [Test]
    public Task Abp() => Compare("https://github.com/abpframework/abp", "10.7.1", "framework/Volo.Abp.slnx");

    private async Task Compare(string url, string tag, string solution, [CallerMemberName] string name = "")
    {
        var source = Path.Combine(temp, "source");
        Git(temp, "clone", "--depth", "1", "--branch", tag, url, source);

        // Package downloads are not what is measured. Every build below is of its own clone, which this leaves nothing in.
        Dotnet(source, null, "restore", solution);

        // Two plain builds at two paths: what differs between them is not mida's doing.
        var plain = Build(source, url, "plain", root => Dotnet(root, null, ["build", solution, .. Relocatable]));
        var again = Build(source, url, "other/plain", root => Dotnet(root, null, ["build", solution, .. Relocatable]));
        var noise = Differences(plain.Files, again.Files).Select(d => d.File).ToHashSet(StringComparer.Ordinal);

        var cache = Path.Combine(temp, "cache");
        var fresh = Build(source, url, "fresh", root => Benchmarks.Mida(root, solution, cache));
        // A clean clone at another path with the cache the build before filled, as a CI runner has it.
        var filled = Build(source, url, "some/deeper/path/filled", root => Benchmarks.Mida(root, solution, cache));

        var seconds = Math.Min(plain.Seconds, again.Seconds);
        var freshDifferences = Differences(plain.Files, fresh.Files).Where(d => !noise.Contains(d.File)).ToList();
        var filledDifferences = Differences(plain.Files, filled.Files).Where(d => !noise.Contains(d.File)).ToList();
        var megabytes = Directory.EnumerateFiles(cache, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) / 1_000_000;
        var report = $"""
            | repo | dotnet build | mida build, empty cache | mida / dotnet | mida build, filled cache | mida / dotnet |
            |---|---|---|---|---|---|
            | {name} {tag} | {seconds:F1} s | {fresh.Seconds:F1} s | {fresh.Seconds / seconds:F2} | {filled.Seconds:F1} s | {filled.Seconds / seconds:F2} |

            The plain build is the faster of two ({plain.Seconds:F1} s and {again.Seconds:F1} s), cache {megabytes} MB, {RuntimeInformation.OSDescription} {RuntimeInformation.OSArchitecture}, {Environment.ProcessorCount} processors

            - Empty cache: {Result(fresh.Output)}
            - Filled cache: {Result(filled.Output)}
            - {plain.Files.Count} files under bin. {noise.Count} of them differ between the two plain builds and are not compared{Kinds(Differences(plain.Files, again.Files))}

            {Section("Empty cache", freshDifferences)}
            {Section("Filled cache", filledDifferences)}
            """;
        var path = Path.Combine(TestContext.ResultsDirectory, $"realworld-{name}");
        await File.WriteAllTextAsync(path + ".md", report);
        await File.WriteAllLinesAsync(path + ".txt", freshDifferences.Select(d => $"empty cache: {d.Kind} {d.File}").Concat(filledDifferences.Select(d => $"filled cache: {d.Kind} {d.File}")));
        TestContext.Current!.Output.WriteLine($"{path}.md\n{report}");

        await Assert.That(freshDifferences).IsEmpty();
        await Assert.That(filledDifferences).IsEmpty();
    }

    /// <summary>Times <paramref name="build"/> in a clean clone at <paramref name="path"/> and returns the hash of every file it left under bin.</summary>
    private (Dictionary<string, string> Files, double Seconds, string Output) Build(string source, string url, string path, Func<string, string> build)
    {
        var root = Path.Combine(temp, path);
        Git(temp, "clone", source, root);
        // SourceLink reads the origin, and a path on this machine is not a repo it knows.
        Git(root, "remote", "set-url", "origin", url);

        // A CI runner starts with no build servers, and a compiler server left by the build before would flatter this one.
        Run(temp, null, "build-server", "shutdown");
        var watch = Stopwatch.StartNew();
        var output = build(root);
        var seconds = watch.Elapsed.TotalSeconds;

        // A built checkout goes as soon as it is hashed: a hosted runner's disk does not hold four of them.
        var files = Outputs(root);
        Directory.Delete(root, recursive: true);
        return (files, seconds, output);
    }

    private static Dictionary<string, string> Outputs(string root)
    {
        var rootBytes = Encoding.UTF8.GetBytes(root);
        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f))
            .Where(f => f.Split(Path.DirectorySeparatorChar) is var parts && parts[0] != ".git" && parts.Contains("bin") && !parts.Contains("node_modules"))
            .AsParallel()
            .ToDictionary(f => f, f => Hash(Path.Combine(root, f), rootBytes), StringComparer.Ordinal);
    }

    /// <summary>
    /// The hash of a file with every mention of its checkout's path replaced. A file that names its own checkout is then
    /// the same in every checkout, and one that a hit brought with another checkout's path in it is not.
    /// </summary>
    private static string Hash(string file, byte[] root)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var rest = File.ReadAllBytes(file).AsSpan();
        for (var at = rest.IndexOf(root); at >= 0; at = rest.IndexOf(root))
        {
            hash.AppendData(rest[..at]);
            hash.AppendData("<root>"u8);
            rest = rest[(at + root.Length)..];
        }

        hash.AppendData(rest);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static List<(string Kind, string File)> Differences(Dictionary<string, string> expected, Dictionary<string, string> actual) =>
    [
        .. expected.Keys.Union(actual.Keys)
            .Where(f => expected.GetValueOrDefault(f) != actual.GetValueOrDefault(f))
            .Order(StringComparer.Ordinal)
            .Select(f => (!actual.ContainsKey(f) ? "missing" : !expected.ContainsKey(f) ? "extra" : "differs", f)),
    ];

    private static string Section(string title, List<(string Kind, string File)> differences)
    {
        const int Shown = 50;
        if (differences.Count == 0)
        {
            return $"{title}: the same files as dotnet build.";
        }

        var lines = differences.Take(Shown).Select(d => $"- {d.Kind} `{d.File}`");
        var more = differences.Count > Shown ? $"\n- and {differences.Count - Shown} more in the .txt beside this file" : string.Empty;
        return $"{title}: {differences.Count} files are not as dotnet build left them{Kinds(differences)}\n\n{string.Join('\n', lines)}{more}\n";
    }

    /// <summary>How many of each kind and extension, which names the cause sooner than a list of files.</summary>
    private static string Kinds(List<(string Kind, string File)> differences) => differences.Count == 0
        ? "."
        : ": " + string.Join(", ", differences.GroupBy(d => $"{d.Kind} {Path.GetExtension(d.File)}").OrderByDescending(g => g.Count()).Select(g => $"{g.Count()} {g.Key}")) + ".";

    private static string Result(string output) =>
        string.Join("; ", output.Split('\n').Where(l => l.Contains(" keys in ", StringComparison.Ordinal) || l.Contains(" hits, ", StringComparison.Ordinal)).Select(l => l.Trim()));

    private static void Git(string directory, params string[] arguments)
    {
        var (exitCode, output) = Start("git", directory, null, arguments);
        if (exitCode != 0)
        {
            Assert.Fail(output);
        }
    }
}
