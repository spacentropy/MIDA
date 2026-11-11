using System.Collections;
using System.Collections.Concurrent;

namespace Mida;

/// <summary>What a run knows of the repo and the machine before it has read a project.</summary>
/// <param name="Hashes">
/// File hashes, one set for the whole run: the files a stored graph asks about are the first ones a key reads.
/// </param>
/// <param name="Variables">
/// The environment as the process found it. MSBuild sets variables for itself once it runs, and a run that needs no
/// MSBuild never sees those, so a question about the environment is always asked of this.
/// </param>
internal sealed record Checkout(
    string RepoRoot,
    string DotnetRoot,
    PathTokens Tokens,
    ConcurrentDictionary<string, string> Hashes,
    IReadOnlyDictionary<string, string> Variables)
{
    public static Checkout Of(string solution, string sdkPath)
    {
        var repoRoot = Mida.RepoRoot.Find(solution);
        var dotnetRoot = Path.GetFullPath(Path.Combine(sdkPath, "..", ".."));
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // Where NuGet keeps packages unless a config moves them, and the graph of a repo with such a config is not stored.
        var packageRoot = Path.GetFullPath(Environment.GetEnvironmentVariable("NUGET_PACKAGES") is { Length: > 0 } packages ? packages : Path.Combine(profile, ".nuget", "packages"));
        // Ignoring case, as MSBuild reads a variable as a property: $(Configuration) sees CONFIGURATION.
        // shortcut: of two variables that differ only in case, the first listed is the one asked. Upgrade if a runner has such a pair.
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry variable in Environment.GetEnvironmentVariables())
        {
            variables.TryAdd((string)variable.Key, (string?)variable.Value ?? string.Empty);
        }

        return new Checkout(repoRoot, dotnetRoot, new PathTokens(repoRoot, packageRoot, dotnetRoot, profile), new ConcurrentDictionary<string, string>(StringComparer.Ordinal), variables);
    }
}
