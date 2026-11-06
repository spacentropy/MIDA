using System.Diagnostics;
using Xunit;

namespace Mida.Tests;

/// <summary>Real restores and builds of a small repo: App → LibA → LibB, and LibA uses Gen at build time.</summary>
public sealed class KeyEndToEndTests : IDisposable
{
    private static readonly string[] Projects = ["App", "LibA", "LibB", "Gen"];

    private readonly string temp = Directory.CreateTempSubdirectory("mida-tests-").FullName;

    public void Dispose() => Directory.Delete(temp, recursive: true);

    private static string Project(string items) =>
        $"""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
          <ItemGroup>{items}</ItemGroup>
        </Project>
        """;

    private static string Reference(string name) => $"""<ProjectReference Include="../{name}/{name}.csproj" />""";

    private static void Write(string root, string path, string content)
    {
        var full = Path.Combine(root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private string Checkout(string relativePath)
    {
        var root = Path.Combine(temp, relativePath);
        Write(root, "Repo.slnx", Solution());
        Write(root, "Directory.Build.props", "<Project><PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup></Project>");
        Write(root, "Directory.Packages.props", """<Project><ItemGroup><PackageVersion Include="Newtonsoft.Json" Version="13.0.3" /></ItemGroup></Project>""");
        Write(root, "App/App.csproj", Project(Reference("LibA")));
        Write(root, "LibA/LibA.csproj", Project(Reference("LibB") + """<ProjectReference Include="../Gen/Gen.csproj" ReferenceOutputAssembly="false" OutputItemType="Analyzer" />"""));
        Write(root, "LibB/LibB.csproj", Project("""<PackageReference Include="Newtonsoft.Json" />"""));
        Write(root, "Gen/Gen.csproj", Project(string.Empty));
        Write(root, "App/App.cs", "public static class App { public static string Name() => LibA.A.Name(); }");
        Write(root, "LibA/A.cs", "namespace LibA; public static class A { public static string Name() => LibB.B.Name(); }");
        Write(root, "LibB/B.cs", """namespace LibB; public static class B { public static string Name() => "b"; }""");
        Write(root, "Gen/G.cs", """namespace Gen; public static class G { public static string Name() => "g"; }""");
        return root;
    }

    private static string Solution(params string[] extra)
    {
        var projects = Projects.Concat(extra)
            .Select(p => $"""<Project Path="{p}/{p}.csproj" />""");
        return $"<Solution>{string.Concat(projects)}</Solution>";
    }

    private static string Dotnet(string directory, string? packages, params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet", arguments)
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (packages is not null)
        {
            start.Environment["NUGET_PACKAGES"] = packages;
        }

        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, output + error.Result);
        return output;
    }

    private static Dictionary<string, string> Keys(string root, string? packages = null)
    {
        var output = Dotnet(root, packages, Path.Combine(AppContext.BaseDirectory, "Mida.dll"), "build", "Repo.slnx", "--print-keys");
        Assert.Contains(" 0 not computable", output, StringComparison.Ordinal);

        return output.Split('\n')
            .Select(line => line.Split(' '))
            .Where(parts => parts is [{ Length: 64 }, _])
            .ToDictionary(parts => parts[1], parts => parts[0]);
    }

    private static HashSet<string> Changed(Dictionary<string, string> before, Dictionary<string, string> after) =>
        [.. after.Where(k => !before.TryGetValue(k.Key, out var value) || value != k.Value)
            .Select(k => Path.GetFileNameWithoutExtension(k.Key))];

    [Fact]
    public void KeysMatchAtDifferentPaths()
    {
        var first = Keys(Checkout("a"));
        var second = Keys(Checkout("some/deeper/path/b"));

        Assert.Equal(4, first.Count);
        Assert.Equal(first, second);
    }

    public static TheoryData<string, string, string, string, string> Edits => new()
    {
        { "body edit stops at the project", "LibB/B.cs", """=> "b";""", """=> "edited";""", "LibB" },
        { "API edit reaches transitive dependents", "LibB/B.cs", "public static string Name", "public static string Added() => string.Empty; public static string Name", "LibB,LibA,App" },
        { "build-time dependency passes on its behaviour", "Gen/G.cs", """=> "g";""", """=> "edited";""", "Gen,LibA,App" },
        { "package version edit", "Directory.Packages.props", "13.0.3", "13.0.2", "App,LibA,LibB,Gen" },
        { "Directory.Build.props edit", "Directory.Build.props", "</PropertyGroup>", "<NoWarn>CS0000</NoWarn></PropertyGroup>", "App,LibA,LibB,Gen" },
    };

    [Theory]
    [MemberData(nameof(Edits))]
    public void EditChangesExactlyTheExpectedKeys(string name, string file, string find, string replace, string expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        var root = Checkout("repo");
        var before = Keys(root);

        var path = Path.Combine(root, file);
        var text = File.ReadAllText(path);
        Assert.Contains(find, text, StringComparison.Ordinal);
        File.WriteAllText(path, text.Replace(find, replace, StringComparison.Ordinal));

        Assert.True(expected.Split(',').ToHashSet().SetEquals(Changed(before, Keys(root))), name);
    }

    [Fact]
    public void JoiningTheSolutionChangesNoOtherKey()
    {
        var root = Checkout("repo");
        var before = Keys(root);

        Write(root, "New/New.csproj", Project(string.Empty));
        Write(root, "Repo.slnx", Solution("New"));

        Assert.Equal(["New"], Changed(before, Keys(root)));
    }

    [Fact]
    public void FloatingVersionResolvingHigherChangesTheKeyWithNoRepoFileChanged()
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

        Assert.Equal(["Lib"], Changed(before, Keys(root, packages)));
    }

#if NET11_0_OR_GREATER
    [Fact]
    public void SdkChangeChangesEveryKey()
    {
        var sdks = Process.Start(new ProcessStartInfo("dotnet", "--list-sdks") { RedirectStandardOutput = true })!.StandardOutput.ReadToEnd();
        var older = sdks.Split('\n').FirstOrDefault(line => line.StartsWith("10.", StringComparison.Ordinal))?.Split(' ')[0];
        Assert.SkipWhen(older is null, "Needs SDK 10 and SDK 11 side by side.");

        var root = Checkout("repo");
        var before = Keys(root);

        Write(root, "global.json", $$"""{ "sdk": { "version": "{{older}}" } }""");

        Assert.Equal(4, Changed(before, Keys(root)).Count);
    }
#endif
}
