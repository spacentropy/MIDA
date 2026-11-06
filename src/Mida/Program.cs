using System.Runtime.CompilerServices;
using Microsoft.Build.Locator;

namespace Mida;

internal static class Program
{
    private const int UsageError = 2;

    private static int Main(string[] args)
    {
        if (args is not ["build", .. var rest] || BuildOptions.Parse(rest) is not { } options)
        {
            Console.Error.WriteLine("usage: mida build [solution] [--print-keys] [--key-details <dir>]");
            return UsageError;
        }

        var sdk = MSBuildLocator.RegisterDefaults();
        return Build(options, sdk.Version.ToString(), sdk.MSBuildPath);
    }

    // Not inlined: the JIT would bind Microsoft.Build types before the Locator registers the SDK.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Build(BuildOptions options, string sdkVersion, string sdkPath) =>
        new BuildRunner(options, sdkVersion, sdkPath).Run();
}
