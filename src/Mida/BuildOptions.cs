using System.CommandLine;

namespace Mida;

internal sealed record BuildOptions(
    string Solution,
    bool PrintKeys,
    string? KeyDetailsDirectory,
    string CacheDirectory,
    IReadOnlyDictionary<string, string> Properties,
    string? Bucket = null,
    string BucketPrefix = "",
    bool ReadOnly = false,
    bool Evaluate = false,
    TestRun? Test = null)
{
    private const string GcsScheme = "gs://";

    private static readonly Argument<string?> SolutionArgument = new("solution")
    {
        Description = "The .sln or .slnx to build. Defaults to the only one in the current directory.",
        Arity = ArgumentArity.ZeroOrOne,
        DefaultValueFactory = _ => FindSolution(),
    };

    private static readonly Option<string?> ConfigurationOption = new("--configuration", "-c")
    {
        Description = "The build configuration.",
    };

    // One property per -p, unlike dotnet build: splitting on ';' would break values such as DefineConstants.
    private static readonly Option<string[]> PropertyOption = new("--property", "-p")
    {
        Description = "A global property, <name>=<value>. One per -p.",
        Validators =
        {
            result =>
            {
                foreach (var token in result.Tokens.Where(t => t.Value.IndexOf('=', StringComparison.Ordinal) <= 0))
                {
                    result.AddError($"-p takes <name>=<value>, not '{token.Value}'.");
                }
            },
        },
    };

    private static readonly Option<string[]> CacheOption = new("--cache")
    {
        Description = "A local store directory, ~/.mida/cache by default, or gs://<bucket>[/<prefix>] for a GCS store behind it.",
        Validators =
        {
            result =>
            {
                foreach (var token in result.Tokens.Where(t => IsRemote(t.Value) && t.Value[GcsScheme.Length..].Split('/')[0].Length == 0))
                {
                    result.AddError($"--cache '{token.Value}' names no bucket.");
                }
            },
        },
    };

    private static readonly Option<bool> ReadOnlyOption = new("--read-only") { Description = "Store nothing in either store." };

    private static readonly Option<bool> EvaluateOption = new("--evaluate")
    {
        Description = "Restore and evaluate every project, even when the store holds a graph that fits. No graph is stored either.",
    };

    private static readonly Option<bool> PrintKeysOption = new("--print-keys") { Description = "Print each project's cache key." };

    private static readonly Option<string?> KeyDetailsOption = new("--key-details")
    {
        Description = "Write the lines of each key to this directory.",
    };

    private static readonly Option<bool> AllOption = new("--all")
    {
        Description = "Run every test project, also one whose pass is in the store.",
    };

    public static Command Command { get; } = CreateCommand("build", "Restore and build a solution through the cache.");

    /// <summary>Everything <c>build</c> takes, and after a <c>--</c> the arguments for each test project's runner, which the parser never sees.</summary>
    public static Command TestCommand { get; } = CreateCommand(
        "test",
        "Build as 'mida build' does, then run the test projects whose pass is not in the store. Arguments after -- go to each test project's runner.",
        AllOption);

    private static string DefaultCacheDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".mida", "cache");

    /// <summary>The arguments after <c>build</c>. Null if they do not parse.</summary>
    public static BuildOptions? Parse(IReadOnlyList<string> args) =>
        Command.Parse(args) is { Errors.Count: 0 } result ? From(result) : null;

    /// <param name="runnerArguments">Not null for <c>mida test</c>: what followed its <c>--</c>.</param>
    public static BuildOptions From(ParseResult result, IReadOnlyList<string>? runnerArguments = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in result.GetValue(PropertyOption) ?? [])
        {
            var equals = property.IndexOf('=', StringComparison.Ordinal);
            properties[property[..equals]] = property[(equals + 1)..];
        }

        if (result.GetValue(ConfigurationOption) is { } configuration)
        {
            properties["Configuration"] = configuration;
        }

        var caches = result.GetValue(CacheOption) ?? [];
        var local = caches.LastOrDefault(c => !IsRemote(c));
        var parts = caches.LastOrDefault(IsRemote)?[GcsScheme.Length..].Split('/', 2);
        var prefix = parts is [_, var folder] && folder.Trim('/') is { Length: > 0 } trimmed ? trimmed + "/" : string.Empty;
        return new BuildOptions(
            Path.GetFullPath(result.GetValue(SolutionArgument)!),
            result.GetValue(PrintKeysOption),
            result.GetValue(KeyDetailsOption) is { } details ? Path.GetFullPath(details) : null,
            local is null ? DefaultCacheDirectory : Path.GetFullPath(local),
            properties,
            parts?[0],
            prefix,
            result.GetValue(ReadOnlyOption),
            result.GetValue(EvaluateOption),
            runnerArguments is null ? null : new TestRun(result.GetValue(AllOption), runnerArguments));
    }

    private static Command CreateCommand(string name, string description, params Option[] more)
    {
        var command = new Command(name, description)
        {
            SolutionArgument, ConfigurationOption, PropertyOption, CacheOption, ReadOnlyOption, EvaluateOption, PrintKeysOption, KeyDetailsOption,
        };
        foreach (var option in more)
        {
            command.Options.Add(option);
        }

        command.Validators.Add(result =>
        {
            // A validator's GetValue skips DefaultValueFactory, so the default is looked up again here.
            if ((result.GetValue(SolutionArgument) ?? FindSolution()) is null)
            {
                result.AddError("No solution given, and the current directory does not hold exactly one .sln or .slnx.");
            }
        });
        return command;
    }

    private static bool IsRemote(string cache) => cache.StartsWith(GcsScheme, StringComparison.Ordinal);

    private static string? FindSolution()
    {
        var found = Directory.EnumerateFiles(Directory.GetCurrentDirectory(), "*.sln*")
            .Where(f => Path.GetExtension(f) is ".sln" or ".slnx")
            .ToList();
        return found.Count == 1 ? found[0] : null;
    }
}
