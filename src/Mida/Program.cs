using System.CommandLine;
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
        return Build(options, Path.GetFileName(Path.TrimEndingDirectorySeparator(sdk.MSBuildPath)), sdk.MSBuildPath);
    }

    // Not inlined: the JIT would bind Microsoft.Build types before the Locator registers the SDK.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Build(BuildOptions options, string sdkVersion, string sdkPath) =>
        new BuildRunner(options, sdkVersion, sdkPath).Run();
}
