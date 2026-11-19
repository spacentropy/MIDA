using System.CommandLine;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Build.Locator;

namespace Mida;

internal static class Program
{
    private static int Main(string[] args)
    {
        // Cut off before parsing: after a -- the parser would take the runner's first argument for the solution.
        // shortcut: a response file is expanded after this, so a -- inside one reaches mida's parser, which then refuses
        // the runner's arguments. Split the expanded tokens when someone needs runner arguments in a response file.
        var separator = args is ["test", ..] ? Array.IndexOf(args, "--") : -1;
        var (own, runner) = separator < 0 ? (args, []) : (args[..separator], args[(separator + 1)..]);

        BuildOptions.Command.SetAction(result => Run(BuildOptions.From(result)));
        BuildOptions.TestCommand.SetAction(result => Run(BuildOptions.From(result, runner)));
        var root = new RootCommand("Builds a .NET repo through MSBuild's static graph with a relocatable output cache, and runs the tests it has not seen pass.")
        {
            BuildOptions.Command, BuildOptions.TestCommand,
        };
        return root.Parse(own).Invoke();
    }

    private static int Run(BuildOptions options)
    {
        // For every process mida starts: NuGet's restore, the worker nodes and the compiler server, which a CI build starts cold.
        // Without PGO's instrumented tier, Abp on the 8-processor pool restored in 11.8 s, not 21.4 s, and built in 60.1 s, not
        // 74.5 s. A value the user set stays. See docs/notes/benchmarks.md
        if (Environment.GetEnvironmentVariable("DOTNET_TieredPGO") is null)
        {
            Environment.SetEnvironmentVariable("DOTNET_TieredPGO", "0");
        }

        var sdk = MSBuildLocator.RegisterDefaults();
        // The folder name keeps the prerelease label that sdk.Version, a System.Version, drops.
        var version = Path.GetFileName(Path.TrimEndingDirectorySeparator(sdk.MSBuildPath));
        var dotnetRoot = Path.GetFullPath(Path.Combine(sdk.MSBuildPath, "..", ".."));

        // Asked beside the build's first steps, which only read: starting dotnet takes as long as finding a stored graph.
        var problem = Task.Run(() => SdkProblem(dotnetRoot, Path.GetDirectoryName(options.Solution)!, version));
        return Build(options, version, sdk.MSBuildPath, problem);
    }

    /// <summary>Null when the loaded SDK is the one the solution's directory selects.</summary>
    private static string? SdkProblem(string dotnetRoot, string directory, string version)
    {
        // The SDK that dotnet resolves for the solution's directory, as dotnet build would pick it.
        var (exitCode, output) = Dotnet(dotnetRoot, directory, ["--version"]);
        var selected = output.Trim();
        if (exitCode != 0)
        {
            return selected;
        }

        // The Locator skips an SDK newer than the runtime it runs on and loads an older one without a word.
        return selected == version
            ? null
            : $"global.json selects SDK {selected}, but mida runs on .NET {Environment.Version} and could only load SDK {version}. Run mida on the runtime of SDK {selected}.";
    }

    /// <summary>Runs the dotnet under <paramref name="dotnetRoot"/> to its end, in this directory when none is given.</summary>
    public static (int ExitCode, string Output) Dotnet(string dotnetRoot, string? directory, IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo(Path.Combine(dotnetRoot, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"), arguments)
        {
            WorkingDirectory = directory ?? string.Empty,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        // Set by a dotnet test above this one, it crashes the reporter of a test application started here.
        start.Environment.Remove("TESTINGPLATFORM_DOTNETTEST_EXECUTIONID");
        using var process = Process.Start(start)!;

        // Both at once: a process that fills one pipe while the other is being read to its end would never finish.
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output + error.GetAwaiter().GetResult());
    }

    // Not inlined: the JIT would bind Microsoft.Build types before the Locator registers the SDK.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Build(BuildOptions options, string sdkVersion, string sdkPath, Task<string?> sdkProblem) =>
        new BuildRunner(options, sdkVersion, sdkPath, sdkProblem).Run();
}
