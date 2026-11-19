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
    /// An Arcade repo: some 480 projects, 74 of them multi-targeted. Its own CI builds through eng/build.sh, but the
    /// solution builds with dotnet build too.
    /// </summary>
    [Test]
    public Task Aspire()
    {
        // Under ContinuousIntegrationBuild, which mida build sets, Arcade moves the package folder into the checkout
        // unless NUGET_PACKAGES names one, and the build then misses what restore put in the default folder.
        if (Environment.GetEnvironmentVariable("NUGET_PACKAGES") is null or "")
        {
            Environment.SetEnvironmentVariable("NUGET_PACKAGES", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages"));
        }

        // Aspire.ProjectTemplates runs tools/scripts/replace-text.cs from a target, and that file-based app builds into
        // Arcade's artifacts/bin. It is no project's output, so a hit, which runs no target, leaves it out.
        return Compare(
            "https://github.com/dotnet/aspire",
            "v17.0.0",
            "Aspire.slnx",
            ignored: ["artifacts/bin/replace-text.cs/"],
            // The playground's AppHosts reference Aspire.Hosting.Analyzers with TargetFramework=netstandard2.0 and the
            // solution without, so plain dotnet build built it twice at once, and on the pool's 22 processors 2 of 4
            // builds failed on its deps.json. This drops the playground's reference.
            properties: ["-p:SkipAspireHostingAnalyzersReference=true"]);
    }

    /// <summary>
    /// OrchardCore's tests, on xUnit v3 and Microsoft.Testing.Platform. Abp's are on VSTest, which mida test refuses.
    /// </summary>
    [Test]
    public Task OrchardCoreTests() => CompareTests(
        "https://github.com/OrchardCMS/OrchardCore",
        "v3.0.1",
        "OrchardCore.slnx",
        mayFail:
        [
            // Needs a Playwright browser, which nothing here installs.
            "OrchardCore.Tests.Functional",
            // Flaky: DefaultPlacementIsReturnedByDefault failed in an earlier run on GitHub and passed on its rerun, and
            // failed in 1 of 3 runs on 2026-11-19. Each filled run is still checked against its own empty run.
            "OrchardCore.Tests",
        ]);

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
            // The same restore from the pool, 2026-11-19, in all three mida runs and not under dotnet test: git could not
            // reach github.com for 131 s.
            "Azure.Mcp.Tools.VirtualDesktop.Tests",
            // Needs pwsh.
            "Azure.Mcp.Server.Tests",
        ],
        // CopilotCliTester downloads the Copilot CLI from an Azure DevOps feed that answers 401 outside Microsoft.
        properties: ["-p:CopilotNpmRegistryUrl=https://registry.npmjs.org"]);

    /// <summary>
    /// A checkout on this machine, cloned so its own tree is left alone: MIDA_LOCAL_REPO is its path, MIDA_LOCAL_SOLUTION
    /// its solution, MIDA_LOCAL_BRANCH the branch, main by default.
    /// </summary>
    [Test]
    public Task Local() => Compare(LocalRepo(), LocalBranch, LocalSolution);

    /// <summary>The same checkout's tests. MIDA_LOCAL_MAYFAIL names the test projects that may fail, comma-separated.</summary>
    [Test]
    public Task LocalTests() => CompareTests(
        LocalRepo(),
        LocalBranch,
        LocalSolution,
        mayFail: Environment.GetEnvironmentVariable("MIDA_LOCAL_MAYFAIL")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? []);

    /// <summary>
    /// The same checkout with the cache filled, then edited in 1 or 10 of its projects, each edit in a clean clone beside
    /// a plain build of it: a comment, which only the edited project must build again, or a public type, which its
    /// dependents must too.
    /// </summary>
    [Test]
    public async Task LocalEdits()
    {
        var url = LocalRepo();
        var source = Source(url, LocalBranch, LocalSolution);
        var filledCache = Path.Combine(temp, "cache");
        var fill = Clone(source, url, "fill", (root, _) => Benchmarks.Mida(root, LocalSolution, filledCache));
        fill.Before();
        fill.After(fill.Run());

        // The solution's projects without the tests, spread over it in path order.
        var projects = SolutionProject().Matches(File.ReadAllText(Path.Combine(source, directory, LocalSolution)))
            .Select(m => m.Groups[1].Value.Replace('\\', '/'))
            .Where(p => !Path.GetFileName(p).Contains("test", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .ToList();
        var (rows, tables) = (new List<string>(), new List<string>());
        foreach (var (scenario, count, api) in new[] { ("BodyEdit1", 1, false), ("BodyEdit10", 10, false), ("ApiEdit1", 1, true), ("ApiEdit10", 10, true) })
        {
            var edited = Enumerable.Range(0, count).Select(i => projects[(2 * i + 1) * projects.Count / (2 * count)]).ToList();
            var output = string.Empty;
            var timed = Timed.Run(
                Benchmarks.Iterations,
                Path.Combine(temp, "BenchmarkDotNet"),
                Clone(source, url, $"{scenario}/plain", (root, _) => Dotnet(root, null, ["build", LocalSolution, .. Relocatable]), before: (root, _) => Edit(root)),
                filledCache: Clone(
                    source,
                    url,
                    $"{scenario}/mida",
                    (root, i) => Benchmarks.Mida(root, LocalSolution, Cache(i)),
                    after: (o, _) => output = o,
                    // Its own copy of the cache, since a run with misses adds to it.
                    before: (root, i) =>
                    {
                        Benchmarks.Copy(filledCache, Cache(i));
                        Edit(root);
                    }));
            rows.Add($"| {scenario} | {string.Join(", ", edited.Select(Path.GetFileNameWithoutExtension))} | {timed.Plain:F1} s | {timed.Filled:F1} s | {timed.Filled / timed.Plain:F2} | {SummaryLines(output)} |");
            tables.Add($"{scenario}:\n\n{timed.Table}");

            string Cache(int iteration) => Path.Combine(temp, $"{scenario}-cache{iteration}");

            void Edit(string root)
            {
                foreach (var project in edited.Select(p => Path.GetDirectoryName(Path.Combine(root, p))!))
                {
                    if (api)
                    {
                        File.WriteAllText(Path.Combine(project, $"Mida{scenario}.cs"), $"namespace MidaEdits;\n\npublic static class {scenario}\n{{\n    public const int Value = 1;\n}}\n");
                    }
                    else
                    {
                        var file = Directory.EnumerateFiles(project, "*.cs", SearchOption.AllDirectories)
                            .Where(f => Path.GetRelativePath(project, f).Split(Path.DirectorySeparatorChar) is var parts && !parts.Contains("bin") && !parts.Contains("obj"))
                            .Order(StringComparer.Ordinal)
                            .First();
                        File.AppendAllText(file, $"\n// {scenario}\n");
                    }
                }
            }
        }

        var report = $"""
            | scenario | edited projects | dotnet build | mida build, filled cache | mida / dotnet | mida, last run |
            |---|---|---|---|---|---|
            {string.Join('\n', rows)}

            Medians of {Benchmarks.Iterations} runs under BenchmarkDotNet, {RuntimeInformation.OSDescription} {RuntimeInformation.OSArchitecture}, {Environment.ProcessorCount} processors

            {string.Join('\n', tables)}
            """;
        var path = Path.Combine(TestContext.ResultsDirectory, "realworld-LocalEdits.md");
        await File.WriteAllTextAsync(path, report);
        TestContext.Current!.Output.WriteLine($"{path}\n{report}");
    }

    [GeneratedRegex("Path=\"([^\"]+\\.csproj)\"")]
    private static partial Regex SolutionProject();

    private static string LocalBranch => Environment.GetEnvironmentVariable("MIDA_LOCAL_BRANCH") is { Length: > 0 } branch ? branch : "main";

    private static string LocalSolution => Environment.GetEnvironmentVariable("MIDA_LOCAL_SOLUTION") ?? string.Empty;

    // MIDA_LOCAL_REPO may be a folder inside the repo, where the solution and its global.json are, and every dotnet
    // command runs there. file:// makes git honour --depth 1 for a path.
    private string LocalRepo()
    {
        Skip.When(Environment.GetEnvironmentVariable("MIDA_LOCAL_REPO") is null or "" || LocalSolution is "", "Needs MIDA_LOCAL_REPO and MIDA_LOCAL_SOLUTION.");
        var path = Path.GetFullPath(Environment.GetEnvironmentVariable("MIDA_LOCAL_REPO")!);
        var top = Start("git", path, null, "rev-parse", "--show-toplevel").Output.Trim();
        directory = Path.GetRelativePath(top, path);
        return new Uri(top).AbsoluteUri;
    }

    // The folder of the checkout that dotnet commands run in.
    private string directory = ".";

    private string Source(string url, string tag, string solution)
    {
        var source = Path.Combine(temp, "source");
        Git(temp, "clone", "--depth", "1", "--branch", tag, url, source);

        // Package downloads are not what is measured. Every build below is of its own clone, which this leaves nothing in.
        Dotnet(Path.Combine(source, directory), null, "restore", solution);
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
        var codes = new int[3];
        var (plain, fresh, filled) = (new List<string>(), new List<string>(), new List<string>());
        properties ??= [];
        var timed = Timed.Run(
            Benchmarks.Iterations,
            Path.Combine(temp, "BenchmarkDotNet"),
            Clone(source, url, "plain", (root, _) => Exited(0, Run(root, null, ["test", "--solution", solution, .. Relocatable, .. properties])), after: (o, _) => plain.Add(o)),
            Clone(source, url, "fresh", (root, i) => Exited(1, Benchmarks.RunMida(root, solution, Cache(i), "test", properties)), after: (o, _) => fresh.Add(o)),
            Clone(source, url, "some/deeper/path/filled", (root, i) => Exited(2, Benchmarks.RunMida(root, solution, Filled(i), "test", properties)), after: (o, _) => filled.Add(o)));
        // mida's own line, not a test's message that says the same, such as mcp's "Test proxy restore failed with exit code 1".
        var failures = fresh.Select(o => o.Split('\n').Where(l => l.Contains("mida: ", StringComparison.Ordinal) && l.Contains(".csproj", StringComparison.Ordinal) && l.Contains(" failed with exit code ", StringComparison.Ordinal)).Select(l => l.Trim()).ToList()).ToList();

        var report = $"""
            | repo | dotnet test | mida test, empty cache | mida / dotnet | mida test, filled cache | mida / dotnet |
            |---|---|---|---|---|---|
            | {name} {tag} | {timed.Plain:F1} s | {timed.Empty:F1} s | {timed.Empty / timed.Plain:F2} | {timed.Filled:F1} s | {timed.Filled / timed.Plain:F2} |

            Medians of {Benchmarks.Iterations} runs under BenchmarkDotNet, last exit codes {codes[0]}, {codes[1]} and {codes[2]}, {RuntimeInformation.OSDescription} {RuntimeInformation.OSArchitecture}, {Environment.ProcessorCount} processors

            - Empty cache, last run: {SummaryLines(fresh[^1])}
            - Filled cache, last run: {SummaryLines(filled[^1])}
            - Failed with the empty cache: {string.Join("; ", failures.Select((f, i) => $"run {i + 1}: {string.Join(", ", f)}"))}

            {LoadTable("plain", "fresh", "some/deeper/path/filled")}

            {timed.Table}
            """;
        var path = Path.Combine(TestContext.ResultsDirectory, $"realworld-{name}");
        await File.WriteAllTextAsync(path + ".md", report);

        // The whole output, which holds a failed test project's own: the report is too short for it.
        await File.WriteAllTextAsync(path + ".txt", string.Join("\n", fresh));
        await File.WriteAllTextAsync(path + ".plain.txt", string.Join("\n", plain));
        TestContext.Current!.Output.WriteLine($"{path}.md\n{report}");

        // What passed with the empty cache passes from the store; only what failed runs again. Each filled run reads the
        // cache of the empty run before it, so a flaky test compares with its own run.
        for (var i = 0; i < fresh.Count; i++)
        {
            var (ran, failed, _) = Counts(fresh[i]);
            var (ranAgain, _, passedBefore) = Counts(filled[i]);
            await Assert.That(ran).IsGreaterThan(0);
            await Assert.That(failures[i].Where(f => !mayFail.Any(m => f.Contains($"/{m}.csproj", StringComparison.Ordinal)))).IsEmpty();
            await Assert.That((ranAgain, passedBefore)).IsEqualTo((failed, ran - failed));
        }

        string Exited(int index, (int ExitCode, string Output) run)
        {
            codes[index] = run.ExitCode;
            return run.Output;
        }
    }

    /// <summary>The cache that iteration <paramref name="iteration"/> of an empty-cache side fills.</summary>
    private string Cache(int iteration) => Path.Combine(temp, $"cache{iteration}");

    /// <summary>The same cache for the filled-cache side, which BenchmarkDotNet runs after the empty-cache one.</summary>
    private string Filled(int iteration) => Directory.Exists(Cache(iteration))
        ? Cache(iteration)
        : throw new InvalidOperationException($"No empty-cache run filled {Cache(iteration)}");

    /// <summary>The numbers of the line that ends a mida test, or -1 each without one.</summary>
    private static (int Ran, int Failed, int PassedBefore) Counts(string output) =>
        TestSummaryLine().Match(output) is { Success: true } match
            ? (Number(match, 1), Number(match, 2), Number(match, 3))
            : (-1, -1, -1);

    private static int Number(Match match, int group) => int.Parse(match.Groups[group].Value, CultureInfo.InvariantCulture);

    [GeneratedRegex(@"(\d+) test projects run, (\d+) failed, (\d+) passed before")]
    private static partial Regex TestSummaryLine();

    /// <summary>
    /// Files under <paramref name="ignored"/>, relative to the checkout, are not compared. Every build gets
    /// <paramref name="properties"/>.
    /// </summary>
    private async Task Compare(string url, string tag, string solution, string[]? ignored = null, string[]? properties = null, [CallerMemberName] string name = "")
    {
        var source = Source(url, tag, solution);
        var (plain, fresh, filled) = (new List<Dictionary<string, string>>(), new List<Dictionary<string, string>>(), new List<Dictionary<string, string>>());
        var outputs = new string[2];
        properties ??= [];
        var timed = Timed.Run(
            Benchmarks.Iterations,
            Path.Combine(temp, "BenchmarkDotNet"),
            Clone(source, url, "plain", (root, _) => Dotnet(root, null, ["build", solution, .. Relocatable, .. properties]), after: (_, root) => plain.Add(Outputs(root))),
            Clone(source, url, "fresh", (root, i) => outputs[0] = Benchmarks.Mida(root, solution, Cache(i), "build", properties), after: (_, root) => fresh.Add(Outputs(root))),
            // A clean clone at another path with the cache the build before filled, as a CI runner has it.
            Clone(source, url, "some/deeper/path/filled", (root, i) => outputs[1] = Benchmarks.Mida(root, solution, Filled(i), "build", properties), after: (_, root) => filled.Add(Outputs(root))));

        // Plain builds at several paths, one per iteration: what differs between them is not mida's doing.
        var plainDifferences = plain.Skip(1).SelectMany(p => Differences(plain[0], p)).Distinct().ToList();
        var noise = plainDifferences.Select(d => d.File).ToHashSet(StringComparer.Ordinal);
        // A name with a hash of its content, such as a fingerprinted .wasm, is another name in every build, so where names
        // came and went between the plain builds, a file that comes or goes says nothing either.
        var renamed = plainDifferences.Where(d => d.Kind != "differs").Select(d => Path.GetDirectoryName(d.File)).ToHashSet(StringComparer.Ordinal);
        bool Compared((string Kind, string File) d) => !noise.Contains(d.File)
            && !(d.Kind != "differs" && renamed.Contains(Path.GetDirectoryName(d.File)))
            && !(ignored ?? []).Any(i => d.File.StartsWith(i, StringComparison.Ordinal));

        // Every mida run beside the first plain build.
        var freshDifferences = fresh.SelectMany(f => Differences(plain[0], f)).Distinct().Where(Compared).ToList();
        var filledDifferences = filled.SelectMany(f => Differences(plain[0], f)).Distinct().Where(Compared).ToList();
        var megabytes = Directory.EnumerateFiles(Cache(0), "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) / 1_000_000;
        var report = $"""
            | repo | dotnet build | mida build, empty cache | mida / dotnet | mida build, filled cache | mida / dotnet |
            |---|---|---|---|---|---|
            | {name} {tag} | {timed.Plain:F1} s | {timed.Empty:F1} s | {timed.Empty / timed.Plain:F2} | {timed.Filled:F1} s | {timed.Filled / timed.Plain:F2} |

            Medians of {Benchmarks.Iterations} runs under BenchmarkDotNet, cache {megabytes} MB, {RuntimeInformation.OSDescription} {RuntimeInformation.OSArchitecture}, {Environment.ProcessorCount} processors

            - Empty cache, last run: {SummaryLines(outputs[0])}
            - Filled cache, last run: {SummaryLines(outputs[1])}
            - {plain[0].Count} files under bin. {noise.Count} of them differ between the {plain.Count} plain builds and are not compared{Kinds(plainDifferences)}{(ignored is { Length: > 0 } ? $" Not compared either: {string.Join(", ", ignored)}" : string.Empty)}

            {Section("Empty cache", freshDifferences)}
            {Section("Filled cache", filledDifferences)}

            {LoadTable("plain", "fresh", "some/deeper/path/filled")}

            {timed.Table}
            """;
        var path = Path.Combine(TestContext.ResultsDirectory, $"realworld-{name}");
        await File.WriteAllTextAsync(path + ".md", report);
        await File.WriteAllLinesAsync(path + ".txt", freshDifferences.Select(d => $"empty cache: {d.Kind} {d.File}").Concat(filledDifferences.Select(d => $"filled cache: {d.Kind} {d.File}")));
        TestContext.Current!.Output.WriteLine($"{path}.md\n{report}");

        await Assert.That(freshDifferences).IsEmpty();
        await Assert.That(filledDifferences).IsEmpty();
    }

    /// <summary>
    /// A side that runs <paramref name="build"/>, given the folder to run in and the iteration, in a clean clone at
    /// <paramref name="path"/>, one folder deeper each iteration. <paramref name="before"/> gets that folder and
    /// <paramref name="after"/> the output and the clone, both untimed.
    /// </summary>
    private Side Clone(string source, string url, string path, Func<string, int, string> build, Action<string, string>? after = null, Action<string, int>? before = null)
    {
        var (root, iteration, ticks) = (string.Empty, -1, Array.Empty<long>());
        return new(
            () =>
            {
                iteration++;
                root = Path.Combine(temp, string.Concat(Enumerable.Repeat("other/", iteration)) + path);
                Git(temp, "clone", source, root);
                // SourceLink reads the origin, and a path on this machine is not a repo it knows.
                Git(root, "remote", "set-url", "origin", url);
                before?.Invoke(Path.Combine(root, directory), iteration);
                ticks = Benchmarks.ShutDown(temp);
            },
            () => build(Path.Combine(root, directory), iteration),
            output =>
            {
                Loads(path).Add(Benchmarks.Load(ticks));
                // A built checkout goes as soon as it is used: a hosted runner's disk does not hold several of them.
                after?.Invoke(output, root);
                Directory.Delete(root, recursive: true);
            });
    }

    // What the machine did during each run of the side at a path, which says whether the build used the processors or
    // waited on the disk. Empty off Linux.
    private readonly Dictionary<string, List<string>> loads = [];

    private List<string> Loads(string path) => loads.TryGetValue(path, out var list) ? list : loads[path] = [];

    private string LoadTable(params string[] paths) =>
        $"| run | {string.Join(" | ", paths.Select(p => $"machine meanwhile, {Path.GetFileName(p)}"))} |\n|---|{string.Concat(paths.Select(_ => "---|"))}\n"
        + string.Join('\n', Loads(paths[0]).Select((_, i) => $"| {i + 1} | {string.Join(" | ", paths.Select(p => Loads(p).ElementAtOrDefault(i)))} |"));

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
