using System.CommandLine;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Build.Locator;

namespace Mida;

internal static class Program
{
    private static int Main(string[] args)
    {
        BuildOptions.Command.SetAction(result => Run(BuildOptions.From(result)));
        var root = new RootCommand("Builds a .NET repo through MSBuild's static graph with a relocatable output cache.") { BuildOptions.Command };
        return root.Parse(args).Invoke();
    }

    private static int Run(BuildOptions options)
    {
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
        var (exitCode, selected) = SelectedSdk(dotnetRoot, directory);
        if (exitCode != 0)
        {
            return selected;
        }

        // The Locator skips an SDK newer than the runtime it runs on and loads an older one without a word.
        return selected == version
            ? null
            : $"global.json selects SDK {selected}, but mida runs on .NET {Environment.Version} and could only load SDK {version}. Run mida on the runtime of SDK {selected}.";
    }

    /// <summary>The SDK that dotnet resolves for the solution's directory, as <c>dotnet build</c> would pick it.</summary>
    private static (int ExitCode, string Output) SelectedSdk(string dotnetRoot, string directory)
    {
        var start = new ProcessStartInfo(Path.Combine(dotnetRoot, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"), "--version")
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, (output + error.Result).Trim());
    }

    // Not inlined: the JIT would bind Microsoft.Build types before the Locator registers the SDK.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Build(BuildOptions options, string sdkVersion, string sdkPath, Task<string?> sdkProblem) =>
        new BuildRunner(options, sdkVersion, sdkPath, sdkProblem).Run();
}
