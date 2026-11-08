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
        var (exitCode, selected) = SelectedSdk(dotnetRoot, Path.GetDirectoryName(options.Solution)!);
        if (exitCode != 0)
        {
            Console.Error.WriteLine(selected);
            return 1;
        }

        // The Locator skips an SDK newer than the runtime it runs on and loads an older one without a word.
        if (selected != version)
        {
            Console.Error.WriteLine(
                $"global.json selects SDK {selected}, but mida runs on .NET {Environment.Version} and could only load SDK {version}. Run mida on the runtime of SDK {selected}.");
            return 1;
        }

        return Build(options, version, sdk.MSBuildPath);
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
    private static int Build(BuildOptions options, string sdkVersion, string sdkPath) =>
        new BuildRunner(options, sdkVersion, sdkPath).Run();
}
