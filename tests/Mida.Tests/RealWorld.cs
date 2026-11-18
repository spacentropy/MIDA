using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
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
public sealed partial class RealWorld : IDisposable
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

    /// <summary>
    /// OrchardCore's tests, on xUnit v3 and Microsoft.Testing.Platform. Abp's are on VSTest, which mida test refuses.
    /// </summary>
    [Test]
    public Task OrchardCoreTests() => CompareTests(
        "https://github.com/OrchardCMS/OrchardCore",
        "v3.0.1",
        "OrchardCore.slnx",
        // Needs a Playwright browser, which nothing here installs.
        mayFail: ["OrchardCore.Tests.Functional"]);

    /// <summary>
    /// The Azure and Fabric MCP servers: 167 projects, none multi-targeted, and 80 test projects on xUnit v3 and
    /// Microsoft.Testing.Platform, most of them replaying recorded Azure calls.
    /// </summary>
    [Test]
    public Task McpTests() => CompareTests(
        "https://github.com/microsoft/mcp",
        "Fabric.Mcp.Server-1.8.0",
        "Microsoft.Mcp.slnx",
        mayFail:
        [
            // Expects the commit in its informational version, which mida build leaves out.
            "Azure.Mcp.Core.Tests",
            // Their recordings come through the test proxy's git restore, which failed on macOS.
            "Azure.Mcp.Tools.ResiliencyAgent.Tests",
            "Azure.Mcp.Tools.Deploy.Tests",
            // Needs pwsh.
            "Azure.Mcp.Server.Tests",
        ],
        // CopilotCliTester downloads the Copilot CLI from an Azure DevOps feed that answers 401 outside Microsoft.
        properties: ["-p:CopilotNpmRegistryUrl=https://registry.npmjs.org"]);

    private string Source(string url, string tag, string solution)
    {
        var source = Path.Combine(temp, "source");
        Git(temp, "clone", "--depth", "1", "--branch", tag, url, source);

        // Package downloads are not what is measured. Every build below is of its own clone, which this leaves nothing in.
        Dotnet(source, null, "restore", solution);
        return source;
    }

    /// <summary>
    /// dotnet test beside mida test with an empty cache, then mida test in a clean clone at another path with the cache
    /// filled, which must run only what failed before. Only the test projects in <paramref name="mayFail"/> may fail.
    /// Every run gets <paramref name="properties"/>.
    /// </summary>
    private async Task CompareTests(string url, string tag, string solution, string[] mayFail, string[]? properties = null, [CallerMemberName] string name = "")
    {
        var source = Source(url, tag, solution);
        var cache = Path.Combine(temp, "cache");
        var codes = new int[3];
        properties ??= [];
        var plain = Build(source, url, "plain", root => Exited(0, Run(root, null, ["test", "--solution", solution, .. Relocatable, .. properties])));
        var fresh = Build(source, url, "fresh", root => Exited(1, Benchmarks.RunMida(root, solution, cache, "test", properties)));
        var filled = Build(source, url, "some/deeper/path/filled", root => Exited(2, Benchmarks.RunMida(root, solution, cache, "test", properties)));
        var failures = fresh.Output.Split('\n').Where(l => l.Contains(" failed with exit code ", StringComparison.Ordinal)).Select(l => l.Trim()).ToList();

        var report = $"""
            | repo | dotnet test | mida test, empty cache | mida / dotnet | mida test, filled cache | mida / dotnet |
            |---|---|---|---|---|---|
            | {name} {tag} | {plain.Seconds:F1} s | {fresh.Seconds:F1} s | {fresh.Seconds / plain.Seconds:F2} | {filled.Seconds:F1} s | {filled.Seconds / plain.Seconds:F2} |

            Exit codes {codes[0]}, {codes[1]} and {codes[2]}, {RuntimeInformation.OSDescription} {RuntimeInformation.OSArchitecture}, {Environment.ProcessorCount} processors

            - Empty cache: {SummaryLines(fresh.Output)}
            - Filled cache: {SummaryLines(filled.Output)}
            - Failed with the empty cache: {string.Join(", ", failures)}
            """;
        var path = Path.Combine(TestContext.ResultsDirectory, $"realworld-{name}");
        await File.WriteAllTextAsync(path + ".md", report);

        // The whole output, which holds a failed test project's own: the report is too short for it.
        await File.WriteAllTextAsync(path + ".txt", fresh.Output);
        await File.WriteAllTextAsync(path + ".plain.txt", plain.Output);
        TestContext.Current!.Output.WriteLine($"{path}.md\n{report}");

        // What passed with the empty cache passes from the store; only what failed runs again.
        var (ran, failed, _) = Counts(fresh.Output);
        var (ranAgain, _, passedBefore) = Counts(filled.Output);
        await Assert.That(ran).IsGreaterThan(0);
        await Assert.That(failures.Where(f => !mayFail.Any(m => f.Contains($"/{m}.csproj", StringComparison.Ordinal)))).IsEmpty();
        await Assert.That((ranAgain, passedBefore)).IsEqualTo((failed, ran - failed));

        string Exited(int index, (int ExitCode, string Output) run)
        {
            codes[index] = run.ExitCode;
            return run.Output;
        }
    }

    /// <summary>The numbers of the line that ends a mida test, or -1 each without one.</summary>
    private static (int Ran, int Failed, int PassedBefore) Counts(string output) =>
        TestSummaryLine().Match(output) is { Success: true } match
            ? (Number(match, 1), Number(match, 2), Number(match, 3))
            : (-1, -1, -1);

    private static int Number(Match match, int group) => int.Parse(match.Groups[group].Value, CultureInfo.InvariantCulture);

    [GeneratedRegex(@"(\d+) test projects run, (\d+) failed, (\d+) passed before")]
    private static partial Regex TestSummaryLine();

    private async Task Compare(string url, string tag, string solution, [CallerMemberName] string name = "")
    {
        var source = Source(url, tag, solution);

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

            - Empty cache: {SummaryLines(fresh.Output)}
            - Filled cache: {SummaryLines(filled.Output)}
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

    private static void Git(string directory, params string[] arguments)
    {
        var (exitCode, output) = Start("git", directory, null, arguments);
        if (exitCode != 0)
        {
            Assert.Fail(output);
        }
    }
}
