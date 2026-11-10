using System.Diagnostics;

namespace Mida.Tests;

/// <summary>A small repo for real builds: App → LibA → LibB, and LibA uses Gen at build time.</summary>
internal static class TestRepo
{
    private static readonly string[] Projects = ["App", "LibA", "LibB", "Gen"];

    public static string Project(string items, string properties = "") =>
        $"""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup><TargetFramework>net10.0</TargetFramework>{properties}</PropertyGroup>
          <ItemGroup>{items}</ItemGroup>
        </Project>
        """;

    /// <summary>
    /// The newest SDK of the runtime under test: the net10.0 build of mida cannot load SDK 11 and refuses to build
    /// with another. The -alpha floor lets a prerelease SDK of that major match.
    /// </summary>
    public static string GlobalJson { get; } = $$"""
        { "sdk": { "version": "{{Environment.Version.Major}}.0.100-alpha", "rollForward": "latestFeature", "allowPrerelease": true }, "test": { "runner": "Microsoft.Testing.Platform" } }
        """;

    /// <summary>A temp directory whose global.json every repo below it inherits.</summary>
    public static string Temp()
    {
        var temp = Directory.CreateTempSubdirectory("mida-tests-").FullName;
        Write(temp, "global.json", GlobalJson);
        return temp;
    }

    public static string Reference(string name) => $"""<ProjectReference Include="../{name}/{name}.csproj" />""";

    public static void Write(string root, string path, string content)
    {
        var full = Path.Combine(root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    public static string Checkout(string root)
    {
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

    public static string Solution(params string[] extra)
    {
        var projects = Projects.Concat(extra)
            .Select(p => $"""<Project Path="{p}/{p}.csproj" />""");
        return $"<Solution>{string.Concat(projects)}</Solution>";
    }

    public static (int ExitCode, string Output) Run(string directory, string? packages, params string[] arguments) =>
        Start("dotnet", directory, packages, arguments);

    public static (int ExitCode, string Output) Start(string file, string directory, string? packages, params string[] arguments)
    {
        var start = new ProcessStartInfo(file, arguments)
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // dotnet test points these at its own SDK, which would mix into the SDK the fixture's global.json selects.
        start.Environment.Remove("MSBuildExtensionsPath");
        start.Environment.Remove("MSBuildSDKsPath");
        if (packages is not null)
        {
            start.Environment["NUGET_PACKAGES"] = packages;
        }

        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output + error.Result);
    }

    public static string Dotnet(string directory, string? packages, params string[] arguments)
    {
        var (exitCode, output) = Run(directory, packages, arguments);
        if (exitCode != 0)
        {
            Assert.Fail(output);
        }

        return output;
    }
}
