using System.Diagnostics;
using System.Globalization;

namespace Mida;

/// <summary>With MIDA_TIMINGS set, says on stderr how long each step of a build took, counted from the start of the process.</summary>
internal static class Phase
{
    private static readonly bool On = Environment.GetEnvironmentVariable("MIDA_TIMINGS") is { Length: > 0 };
    private static readonly DateTime Start = On ? Process.GetCurrentProcess().StartTime.ToUniversalTime() : default;
    private static double last;

    /// <summary>Ends the step that began at the mark before, under this name.</summary>
    public static void Mark(string name)
    {
        if (!On)
        {
            return;
        }

        var now = (DateTime.UtcNow - Start).TotalSeconds;
        Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{name,-28} {now - last,7:F3} s  (at {now:F3})"));
        last = now;
    }

    /// <summary>Says whether the run loaded MSBuild, which a build that hits everything from a stored graph must not.</summary>
    public static void End()
    {
        if (On)
        {
            var loaded = AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "Microsoft.Build");
            Console.Error.WriteLine(loaded ? "MSBuild was loaded" : "MSBuild was not loaded");
        }
    }
}
