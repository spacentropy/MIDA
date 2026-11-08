using System.Diagnostics;
using static Mida.Tests.TestRepo;

namespace Mida.Tests;

/// <summary>Real restores and builds of a small repo: App → LibA → LibB, and LibA uses Gen at build time.</summary>
public sealed class KeyEndToEndTests : IDisposable
{
    private readonly string temp = Temp();

    public void Dispose() => Directory.Delete(temp, recursive: true);

    private string Checkout(string relativePath) => TestRepo.Checkout(Path.Combine(temp, relativePath));

    private Dictionary<string, string> Keys(string root, string? packages = null, params string[] arguments)
    {
        var output = Dotnet(root, packages, [Path.Combine(AppContext.BaseDirectory, "Mida.dll"), "build", "Repo.slnx", "--print-keys", "--cache", Path.Combine(temp, "cache"), .. arguments]);
        if (!output.Contains(" 0 not computable", StringComparison.Ordinal))
        {
            Assert.Fail(output);
        }

        return output.Split('\n')
            .Select(line => line.Split(' '))
            .Where(parts => parts is [{ Length: 64 }, _])
            .ToDictionary(parts => parts[1], parts => parts[0]);
    }

    private static HashSet<string> Changed(Dictionary<string, string> before, Dictionary<string, string> after) =>
        [.. after.Where(k => !before.TryGetValue(k.Key, out var value) || value != k.Value)
            .Select(k => Path.GetFileNameWithoutExtension(k.Key))];

    [Test]
    public async Task KeysMatchAtDifferentPaths()
    {
        var first = Keys(Checkout("a"));
        var second = Keys(Checkout("some/deeper/path/b"));

        await Assert.That(first.Count).IsEqualTo(4);
        await Assert.That(second).IsEquivalentTo(first);
    }

    [Test]
    [Arguments("-c", "Release")]
    [Arguments("-p:Foo=bar", null)]
    public async Task GlobalPropertyChangesEveryKey(string option, string? value)
    {
        var root = Checkout("repo");
        var before = Keys(root);

        var after = Keys(root, null, value is null ? [option] : [option, value]);

        await Assert.That(Changed(before, after)).IsEquivalentTo(["App", "LibA", "LibB", "Gen"]);
    }

    [Test]
    [Arguments("body edit stops at the project", "LibB/B.cs", """=> "b";""", """=> "edited";""", "LibB")]
    [Arguments("API edit reaches transitive dependents", "LibB/B.cs", "public static string Name", "public static string Added() => string.Empty; public static string Name", "LibB,LibA,App")]
    [Arguments("build-time dependency passes on its behaviour", "Gen/G.cs", """=> "g";""", """=> "edited";""", "Gen,LibA,App")]
    [Arguments("package version edit", "Directory.Packages.props", "13.0.3", "13.0.2", "App,LibA,LibB,Gen")]
    [Arguments("Directory.Build.props edit", "Directory.Build.props", "</PropertyGroup>", "<NoWarn>CS0000</NoWarn></PropertyGroup>", "App,LibA,LibB,Gen")]
    public async Task EditChangesExactlyTheExpectedKeys(string name, string file, string find, string replace, string expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        var root = Checkout("repo");
        var before = Keys(root);

        var path = Path.Combine(root, file);
        var text = File.ReadAllText(path);
        await Assert.That(text).Contains(find);
        File.WriteAllText(path, text.Replace(find, replace, StringComparison.Ordinal));

        await Assert.That(Changed(before, Keys(root))).IsEquivalentTo(expected.Split(',')).Because(name);
    }

    [Test]
    public async Task BuildTimeDependencyReachedNormallyFirstStillPassesOnItsBehaviour()
    {
        var root = Checkout("repo");
        // LibA lists LibB before Gen, so the walk meets Gen through LibB's normal reference first.
        Write(root, "LibB/LibB.csproj", Project("""<PackageReference Include="Newtonsoft.Json" />""" + Reference("Gen")));
        var before = Keys(root);

        var path = Path.Combine(root, "Gen/G.cs");
        File.WriteAllText(path, File.ReadAllText(path).Replace("""=> "g";""", """=> "edited";""", StringComparison.Ordinal));

        await Assert.That(Changed(before, Keys(root))).IsEquivalentTo(["Gen", "LibA", "App"]);
    }

    [Test]
    public async Task RestoreSeesTheInjectedProperties()
    {
        var root = Checkout("repo");
        Write(root, "LibB/LibB.csproj", Project("""<PackageReference Include="Newtonsoft.Json" Condition="'$(ContinuousIntegrationBuild)' == 'true'" />"""));
        Write(root, "LibB/B.cs", "namespace LibB; public static class B { public static string Name() => Newtonsoft.Json.JsonConvert.SerializeObject(\"b\"); }");

        await Assert.That(Keys(root).Count).IsEqualTo(4);
    }

    [Test]
    public async Task NewFileInADependencysOutputChangesDependentsKeys()
    {
        var root = Checkout("repo");
        var before = Keys(root);

        Write(root, "LibB/Strings.fr.resx", """
            <root>
              <resheader name="resmimetype"><value>text/microsoft-resx</value></resheader>
              <resheader name="version"><value>2.0</value></resheader>
              <resheader name="reader"><value>System.Resources.ResXResourceReader, System.Windows.Forms</value></resheader>
              <resheader name="writer"><value>System.Resources.ResXResourceWriter, System.Windows.Forms</value></resheader>
              <data name="Hello"><value>Bonjour</value></data>
            </root>
            """);

        await Assert.That(Changed(before, Keys(root))).IsEquivalentTo(["LibB", "LibA", "App"]);
    }

    [Test]
    public async Task JoiningTheSolutionChangesNoOtherKey()
    {
        var root = Checkout("repo");
        var before = Keys(root);

        Write(root, "New/New.csproj", Project(string.Empty));
        Write(root, "Repo.slnx", Solution("New"));

        await Assert.That(Changed(before, Keys(root))).IsEquivalentTo(["New"]);
    }

    [Test]
    public async Task FloatingVersionResolvingHigherChangesTheKeyWithNoRepoFileChanged()
    {
        var feed = Path.Combine(temp, "feed");
        var source = Path.Combine(temp, "source");
        var packages = Path.Combine(temp, "packages");
        Directory.CreateDirectory(feed);
        Write(source, "Mida.Test.Floating.csproj", Project(string.Empty));
        Write(source, "C.cs", "namespace Floating; public static class C { }");
        Dotnet(source, packages, "pack", "-p:Version=1.0.0", "-o", feed);

        var root = Path.Combine(temp, "repo");
        Write(root, "nuget.config", $"""<configuration><packageSources><clear /><add key="feed" value="{feed}" /></packageSources></configuration>""");
        Write(root, "Repo.slnx", """<Solution><Project Path="Lib/Lib.csproj" /></Solution>""");
        Write(root, "Lib/Lib.csproj", Project("""<PackageReference Include="Mida.Test.Floating" Version="1.*" />"""));
        var before = Keys(root, packages);

        Dotnet(source, packages, "pack", "-p:Version=1.1.0", "-o", feed);
        Directory.Delete(Path.Combine(root, "Lib", "obj"), recursive: true);

        await Assert.That(Changed(before, Keys(root, packages))).IsEquivalentTo(["Lib"]);
    }

#if NET11_0_OR_GREATER
    [Test]
    public async Task SdkChangeChangesEveryKey()
    {
        var sdks = Process.Start(new ProcessStartInfo("dotnet", "--list-sdks") { RedirectStandardOutput = true })!.StandardOutput.ReadToEnd();
        var older = sdks.Split('\n').FirstOrDefault(line => line.StartsWith("10.", StringComparison.Ordinal))?.Split(' ')[0];
        Skip.When(older is null, "Needs SDK 10 and SDK 11 side by side.");

        var root = Checkout("repo");
        var before = Keys(root);

        Write(root, "global.json", $$"""{ "sdk": { "version": "{{older}}" } }""");

        await Assert.That(Changed(before, Keys(root)).Count).IsEqualTo(4);
    }
#endif
}
