using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Xml;
using Microsoft.Build.Execution;
using Microsoft.Build.FileSystem;
using Microsoft.Build.Framework;
using Microsoft.Build.Graph;
using Microsoft.Build.Prediction;

namespace Mida;

/// <param name="NotStorable">Why no later run may take this graph for its own, or null.</param>
/// <param name="RestoreNotReusable">Why a later run that takes this graph must still restore, or null.</param>
internal sealed record Evaluated(
    IReadOnlyList<string> Environment,
    IReadOnlyList<SnapshotProject> Projects,
    SnapshotQuestions Questions,
    string? NotStorable,
    string? RestoreNotReusable);

/// <param name="Unrecorded">How the file reads the machine without asking anything a question records, or null.</param>
/// <param name="Properties">The names of the properties it reads outside targets.</param>
internal sealed record ImportScan(string? Unrecorded, IReadOnlySet<string> Properties);

/// <summary>Turns a graph MSBuild evaluated into the form that is stored, and collects what the evaluation depended on.</summary>
internal static partial class GraphEvaluation
{
    // What NuGet's static graph restore builds in each project, and so all that runs of a restore besides NuGet itself.
    private static readonly string[] RestoreTargets = ["_IsProjectRestoreSupported", "_CollectRestoreInputs"];

    // Global properties a restore's evaluation has and the build's does not. A file that reads one can import or
    // declare something else for restore than for the build, and only the build's evaluation is recorded.
    private static readonly string[] RestoreOnlyProperties = ["ExcludeRestorePackageImports", "MSBuildRestoreSessionId", "MSBuildIsRestoring", "RestoreUseStaticGraphEvaluation"];

    // NuGet reads these itself, and they decide what a restore writes: the package folder is in every generated props file.
    private static readonly string[] RestoreVariables = ["NUGET_PACKAGES", "NUGET_FALLBACK_PACKAGES"];

    private static readonly string[] ExcludedRootProperties = ["NuGetPackageRoot", "BaseIntermediateOutputPath", "BaseOutputPath"];

    private static readonly string[] OwnedRootProperties = ["BaseOutputPath", "BaseIntermediateOutputPath"];

    // The predictors of Microsoft.Build.Prediction 1.2.44 that read files or directories no question covers, by their
    // source. A graph one of them took part in is not stored.
    public static readonly IReadOnlySet<string> UnfollowedPredictors = new HashSet<string>(StringComparer.Ordinal)
    {
        "ArtifactsSdkPredictor",
        "AzureCloudServiceWorkerFilesPredictor",
        "ClIncludeItemsPredictor",
        "CodeAnalysisRuleSetPredictor",
        "ServiceFabricPackageRootFilesGraphPredictor",
        "ServiceFabricServiceManifestPredictor",
        "StyleCopPredictor",
        "TsConfigPredictor",
    };

    // The ones that read only the evaluated project, and three whose reads Ask repeats: DotnetSdkPredictor finds the
    // nearest global.json, GenerateRuntimeConfigurationFilesPredictor looks for the UserRuntimeConfig file, and
    // ReferenceItemsPredictor for each Reference item's file and its related files. A predictor in neither list is
    // new, and GraphEvaluationTests fails until someone has read it.
    public static readonly IReadOnlySet<string> FollowedPredictors = new HashSet<string>(StringComparer.Ordinal)
    {
        "AdditionalIncludeDirectoriesPredictor",
        "AnalyzerItemsPredictor",
        "ApplicationDefinitionItemsPredictor",
        "ApplicationIconPredictor",
        "AssemblyOriginatorKeyFilePredictor",
        "AvailableItemNameItemsPredictor",
        "AzureCloudServicePipelineTransformPhaseGraphPredictor",
        "AzureCloudServicePredictor",
        "CompiledAssemblyPredictor",
        "CompileItemsPredictor",
        "ContentItemsPredictor",
        "CopyTaskPredictor",
        "CppContentFilesProjectOutputGroupPredictor",
        "DocumentationFilePredictor",
        "DotnetSdkPredictor",
        "EditorConfigFilesItemsPredictor",
        "EmbeddedResourceItemsPredictor",
        "FakesPredictor",
        "GenerateBuildDependencyFilePredictor",
        "GeneratePackageOnBuildPredictor",
        "GeneratePublishDependencyFilePredictor",
        "GenerateRuntimeConfigurationFilesPredictor",
        "GetCopyToOutputDirectoryItemsGraphPredictor",
        "GetCopyToPublishDirectoryItemsGraphPredictor",
        "IntermediateOutputPathPredictor",
        "LinkItemsPredictor",
        "ManifestsPredictor",
        "MasmItemsPredictor",
        "ModuleDefinitionFilePredictor",
        "NoneItemsPredictor",
        "OutDirOrOutputPathPredictor",
        "PageItemsPredictor",
        "ProjectFileAndImportsGraphPredictor",
        "ProjectFileAndImportsPredictor",
        "RefAssemblyPredictor",
        "ReferenceItemsPredictor",
        "ResourceItemsPredictor",
        "ServiceFabricCopyFilesToPublishDirectoryGraphPredictor",
        "SplashScreenItemsPredictor",
        "SqlBuildPredictor",
        "SymbolsFilePredictor",
        "TypeScriptCompileItemsPredictor",
        "VSCTCompileItemsPredictor",
        "XamlAppDefPredictor",
    };

    // Ways a project file reads the machine without asking MSBuild's file system, so no question records them.
    private static readonly string[] UnrecordedReads = ["->Exists(", "[System.IO.File]::", "[System.IO.Directory]::", "[System.Environment]::"];

    private static readonly char[] InvalidPathCharacters = Path.GetInvalidPathChars();

    public static Evaluated Read(
        ProjectGraph graph,
        Questions questions,
        PathTokens tokens,
        string solution,
        string dotnetRoot,
        IReadOnlyList<string> environment)
    {
        var predictions = new ProjectGraphPredictionExecutor(ProjectPredictors.AllProjectGraphPredictors, ProjectPredictors.AllProjectPredictors)
            .PredictInputsAndOutputs(graph).PredictionsPerNode;
        var targets = graph.GetTargetLists(["Build"]);

        // What a key must not read: the SDK and packages, which the key covers by version, and every project's outputs.
        List<string> excludedRoots =
        [
            KeyComputer.WithSeparator(dotnetRoot),
            .. graph.ProjectNodes
                .SelectMany(n => ExcludedRootProperties.Select(p => Absolute(n.ProjectInstance, p)))
                .OfType<string>()
                .Select(KeyComputer.WithSeparator)
                .Distinct(StringComparer.Ordinal),
        ];

        var order = DependenciesFirst(graph);
        var indexes = order.Select((node, index) => (node, index)).ToDictionary(n => n.node, n => n.index);
        var projects = new SnapshotProject[order.Count];
        Parallel.For(0, order.Count, i => projects[i] = Store(order[i], predictions[order[i]], targets[order[i]], indexes, excludedRoots, tokens));

        var (notStorable, restoreNotReusable) = Ask(graph, predictions, questions, tokens, solution, dotnetRoot);
        var stored = questions.Store(tokens, projects.SelectMany(Paths).Any(PathTokens.IsPlaced));
        return new Evaluated(environment, projects, stored, notStorable ?? questions.Unanswerable, restoreNotReusable);
    }

    private static IEnumerable<string> Paths(SnapshotProject project) =>
        new[] { project.Path, project.AssetsFile, project.TargetRefPath, project.TargetPath, project.OutDir, project.PackageRoot, project.ExtensionsPath }
            .OfType<string>()
            .Concat(project.Inputs)
            .Concat(project.BuildTimeReferences)
            .Concat(project.OutputDirectories)
            .Concat(project.OwnedRoots);

    private static SnapshotProject Store(
        ProjectGraphNode node,
        ProjectPredictions predictions,
        IEnumerable<string> targets,
        Dictionary<ProjectGraphNode, int> indexes,
        List<string> excludedRoots,
        PathTokens tokens)
    {
        var project = node.ProjectInstance;
        string? Token(string? path) => path is null ? null : tokens.TokenizePath(path);
        return new SnapshotProject(
            tokens.TokenizePath(project.FullPath),
            [.. targets],
            new SortedDictionary<string, string>(
                project.GlobalProperties.Where(p => KeyComputer.IsKeyed(p.Key)).ToDictionary(p => p.Key, p => tokens.Tokenize(p.Value)), StringComparer.Ordinal),
            [
                .. predictions.InputFiles
                    .Select(f => Path.GetFullPath(f.Path))
                    .Where(f => !excludedRoots.Any(root => f.StartsWith(root, KeyComputer.PathComparison)))
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .Select(tokens.TokenizePath),
            ],
            Token(Absolute(project, "ProjectAssetsFile")),
            [.. node.ProjectReferences.Select(r => indexes[r])],
            [
                .. project.GetItems("ProjectReference")
                    .Where(r => r.GetMetadataValue("OutputItemType").Length > 0
                        || r.GetMetadataValue("ReferenceOutputAssembly").Equals("false", StringComparison.OrdinalIgnoreCase))
                    .Select(r => KeyComputer.AbsolutePath(project.Directory, r.EvaluatedInclude))
                    .OfType<string>()
                    .Select(tokens.TokenizePath),
            ],
            project.GetPropertyValue("IsCrossTargetingBuild") == "true",
            Token(Absolute(project, "TargetRefPath")),
            Token(Absolute(project, "TargetPath")),
            tokens.TokenizePath(KeyComputer.WithSeparator(Absolute(project, "OutDir") ?? project.Directory)),
            [
                .. predictions.OutputDirectories
                    .Select(d => KeyComputer.AbsolutePath(project.Directory, d.Path))
                    .OfType<string>()
                    .Select(KeyComputer.WithSeparator)
                    .Select(tokens.TokenizePath),
            ],
            [.. OwnedRootProperties.Select(p => Absolute(project, p)).OfType<string>().Select(KeyComputer.WithSeparator).Select(tokens.TokenizePath)],
            [.. OwnCopies(project)],
            project.GetPropertyValue("NuGetPackageRoot") is { Length: > 0 } packages ? tokens.TokenizePath(packages) : null,
            Token(Absolute(project, "MSBuildProjectExtensionsPath")));
    }

    private static string? Absolute(ProjectInstance project, string property) =>
        KeyComputer.AbsolutePath(project.Directory, project.GetPropertyValue(property));

    /// <summary>Where the project's own items copy to in <c>bin/</c>, by the rule of MSBuild's AssignTargetPath.</summary>
    private static IEnumerable<string> OwnCopies(ProjectInstance project) => project.Items
        .Where(i => i.GetMetadataValue("CopyToOutputDirectory") is { Length: > 0 } copy && !copy.Equals("Never", StringComparison.OrdinalIgnoreCase))
        .Select(i => (i.GetMetadataValue("TargetPath") is { Length: > 0 } target ? target
            : i.GetMetadataValue("Link") is { Length: > 0 } link ? link
            : i.GetMetadataValue("FullPath") is var full && full.StartsWith(KeyComputer.WithSeparator(project.Directory), KeyComputer.PathComparison) ? Path.GetRelativePath(project.Directory, full)
            : Path.GetFileName(full)).Replace('\\', Path.DirectorySeparatorChar))
        .Distinct(StringComparer.FromComparison(KeyComputer.PathComparison));

    private static List<ProjectGraphNode> DependenciesFirst(ProjectGraph graph)
    {
        var order = new List<ProjectGraphNode>(graph.ProjectNodes.Count);
        var seen = new HashSet<ProjectGraphNode>();
        void Visit(ProjectGraphNode node)
        {
            if (seen.Add(node))
            {
                foreach (var reference in node.ProjectReferences)
                {
                    Visit(reference);
                }

                order.Add(node);
            }
        }

        // By path, so one commit stores the same graph on every machine.
        foreach (var node in graph.ProjectNodes.OrderBy(n => n.ProjectInstance.FullPath, StringComparer.Ordinal))
        {
            Visit(node);
        }

        return order;
    }

    /// <summary>
    /// The questions MSBuild's file system never hears: files it reads itself, and what the SDK and the predictors ask
    /// the disk directly. Returns why the graph cannot be stored, when something asks in a way that is not followed,
    /// and why its restore cannot be skipped, when restore depends on something the build's evaluation did not read.
    /// </summary>
    private static (string? NotStorable, string? RestoreNotReusable) Ask(
        ProjectGraph graph,
        IReadOnlyDictionary<ProjectGraphNode, ProjectPredictions> predictions,
        Questions questions,
        PathTokens tokens,
        string solution,
        string dotnetRoot)
    {
        string? notStorable = null;
        string? restoreNotReusable = null;
        var dotnetPrefix = KeyComputer.WithSeparator(dotnetRoot);
        var imports = new HashSet<string>(StringComparer.Ordinal);
        var directories = new HashSet<string>(StringComparer.Ordinal) { Path.GetDirectoryName(solution)! };
        foreach (var node in graph.ProjectNodes)
        {
            var project = node.ProjectInstance;
            imports.Add(project.FullPath);
            imports.UnionWith(project.ImportPaths);
            directories.Add(project.Directory);
            restoreNotReusable ??= RestoreOutsideTheSdk(project, dotnetPrefix, KeyComputer.WithSeparator(tokens.PackageRoot));

            // A lock file is NuGet's input, not MSBuild's, and a restore that a later run skips has to have read the same one.
            questions.AddFile(Path.Combine(project.Directory, "packages.lock.json"));
            questions.AddFile(Path.Combine(project.Directory, $"packages.{project.GetPropertyValue("MSBuildProjectName")}.lock.json"));
            if (Absolute(project, "NuGetLockFilePath") is { } lockFile)
            {
                questions.AddFile(lockFile);
            }

            // The SDK finds .editorconfig and .globalconfig with the Exists item function, which reads the disk itself.
            foreach (var candidate in project.GetItems("PotentialEditorConfigFiles").Concat(project.GetItems("GlobalAnalyzerConfigFiles")))
            {
                questions.AddExists(Questions.Either, candidate.GetMetadataValue("FullPath"));
            }

            // GenerateRuntimeConfigurationFilesPredictor looks for this file.
            if (project.GetPropertyValue("UserRuntimeConfig") is { Length: > 0 } runtimeConfig)
            {
                questions.AddExists(Questions.Files, Path.Combine(project.Directory, runtimeConfig));
            }

            // ReferenceItemsPredictor looks for each referenced file and for what lies beside it, such as its .pdb. Every
            // netstandard2.0 project has such items, from the NETStandard.Library package.
            var related = project.GetPropertyValue("AllowedReferenceRelatedFileExtensions").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var reference in project.GetItems("Reference"))
            {
                var file = reference.GetMetadataValue("HintPath") is { Length: > 0 } hint ? hint : reference.EvaluatedInclude;
                if (file.IndexOfAny(InvalidPathCharacters) < 0)
                {
                    var full = Path.GetFullPath(Path.Combine(project.Directory, file));
                    questions.AddExists(Questions.Files, full);
                    foreach (var extension in related)
                    {
                        questions.AddExists(Questions.Files, Path.ChangeExtension(full, extension));
                    }
                }
            }

            // Only then does the SDK list the project directory for solutions and projects, with System.IO.
            if (project.GetPropertyValue("DisableDefaultItemsInProjectFolder").Equals("true", StringComparison.OrdinalIgnoreCase))
            {
                notStorable ??= $"{project.FullPath} sets DisableDefaultItemsInProjectFolder";
            }

            if (project.GetPropertyValue("NuGetPackageRoot") is { Length: > 0 } packages
                && !Path.TrimEndingDirectorySeparator(Path.GetFullPath(packages)).Equals(tokens.PackageRoot, KeyComputer.PathComparison))
            {
                notStorable ??= $"the package folder {packages} is neither NUGET_PACKAGES nor the default";
            }

            var own = predictions[node];
            var unfollowed = new[] { own.InputFiles, own.InputDirectories, own.OutputFiles, own.OutputDirectories }
                .SelectMany(items => items)
                .SelectMany(item => item.PredictedBy)
                .FirstOrDefault(predictor => !FollowedPredictors.Contains(predictor));
            if (unfollowed is not null)
            {
                notStorable ??= $"{unfollowed} reads files for {project.FullPath} that are not followed";
            }
        }

        // The restore outputs among the imports come back from the store, or are compared after a restore.
        var packagePrefix = KeyComputer.WithSeparator(tokens.PackageRoot);
        foreach (var import in imports.Where(i => !ProjectOutputs.IsRestored(Path.GetFileName(i))
            && !i.StartsWith(dotnetPrefix, KeyComputer.PathComparison)
            && !i.StartsWith(packagePrefix, KeyComputer.PathComparison)))
        {
            questions.AddFile(import);
        }

        var scans = new ConcurrentDictionary<string, ImportScan>(StringComparer.Ordinal);
        Parallel.ForEach(imports, import => scans[import] = Scan(import));
        notStorable ??= scans.Values.Select(s => s.Unrecorded).OfType<string>().Order(StringComparer.Ordinal).FirstOrDefault();

        // MSBuild says when it reads a variable that is set, and not when a property that a variable could set is
        // unset. So every name read anywhere is a question while no variable has it: setting one later is a change.
        foreach (var property in scans.Values.SelectMany(s => s.Properties))
        {
            questions.AddUnset(property);
        }

        foreach (var variable in RestoreVariables)
        {
            questions.AddEnvironment(variable);
        }

        // NuGet's own props read ExcludeRestorePackageImports, and packages are versioned like the SDK.
        restoreNotReusable ??= scans
            .Where(s => !s.Key.StartsWith(dotnetPrefix, KeyComputer.PathComparison)
                && !s.Key.StartsWith(packagePrefix, KeyComputer.PathComparison)
                && !ProjectOutputs.IsRestored(Path.GetFileName(s.Key)))
            .Select(s => (File: s.Key, Property: RestoreOnlyProperties.FirstOrDefault(s.Value.Properties.Contains)))
            .Where(s => s.Property is not null)
            .Select(s => $"{s.File} reads {s.Property}")
            .Order(StringComparer.Ordinal)
            .FirstOrDefault();

        foreach (var directory in directories)
        {
            // Both the SDK resolver and DotnetSdkPredictor take the nearest global.json and look no further.
            foreach (var globalJson in Ancestors(directory).Select(d => Path.Combine(d, "global.json")))
            {
                questions.AddFile(globalJson);
                if (File.Exists(globalJson))
                {
                    break;
                }
            }
        }

        // Restore builds the solution itself, which imports these. No project does, so no evaluation here read them.
        var solutionDirectory = Path.GetDirectoryName(solution)!;
        foreach (var name in new[] { $"before.{Path.GetFileName(solution)}.targets", $"after.{Path.GetFileName(solution)}.targets" })
        {
            questions.AddFile(Path.Combine(solutionDirectory, name));
        }

        foreach (var directory in Ancestors(solutionDirectory))
        {
            questions.AddFile(Path.Combine(directory, "Directory.Solution.props"));
            questions.AddFile(Path.Combine(directory, "Directory.Solution.targets"));
        }

        if (Path.GetExtension(solution).Equals(".slnf", StringComparison.OrdinalIgnoreCase))
        {
            notStorable ??= $"{solution} is a solution filter, and the solution it names is not followed";
        }

        return (notStorable, restoreNotReusable);
    }

    /// <summary>
    /// A target that runs in the project's restore and is not the SDK's, or null. Its body is not evaluation, so what
    /// it reads, such as a file it takes package references from, is in no question. The SDK's are fixed by its version.
    /// So are those in the package folder: restore leaves out what packages import, and an MSBuild SDK from there is
    /// pinned in global.json.
    /// </summary>
    private static string? RestoreOutsideTheSdk(ProjectInstance project, string dotnetPrefix, string packagePrefix)
    {
        if (project.GlobalProperties.TryGetValue("RestoreUseStaticGraphEvaluation", out var staticGraph)
            && !staticGraph.Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            return "restore does not use static graph evaluation, so which targets it runs is not followed";
        }

        IEnumerable<string> Names(string list) =>
            (list.Contains('$', StringComparison.Ordinal) || list.Contains('@', StringComparison.Ordinal) ? project.ExpandString(list) : list)
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Which targets run before or after each target, the way MSBuild reads BeforeTargets and AfterTargets.
        var hooks = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, target) in project.Targets)
        {
            foreach (var hooked in Names(target.BeforeTargets).Concat(Names(target.AfterTargets)))
            {
                (hooks.TryGetValue(hooked, out var list) ? list : hooks[hooked] = []).Add(name);
            }
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<string>(RestoreTargets);
        while (pending.TryDequeue(out var name))
        {
            if (!seen.Add(name))
            {
                continue;
            }

            if (project.Targets.TryGetValue(name, out var target))
            {
                if (!target.FullPath.StartsWith(dotnetPrefix, KeyComputer.PathComparison) && !target.FullPath.StartsWith(packagePrefix, KeyComputer.PathComparison))
                {
                    return $"{target.FullPath} has the target {name}, which runs in restore";
                }

                foreach (var dependency in Names(target.DependsOnTargets))
                {
                    pending.Enqueue(dependency);
                }
            }

            foreach (var hook in hooks.GetValueOrDefault(name) ?? [])
            {
                pending.Enqueue(hook);
            }
        }

        return null;
    }

    private static IEnumerable<string> Ancestors(string directory)
    {
        for (var current = directory; current is not null; current = Path.GetDirectoryName(current))
        {
            yield return current;
        }
    }

    /// <summary>
    /// What a file reads outside targets: the properties it names, and any read of the machine that no question records.
    /// The two such reads the SDK makes are known, and asked in <see cref="Ask"/>: one for .editorconfig files, one
    /// behind DisableDefaultItemsInProjectFolder.
    /// </summary>
    internal static ImportScan Scan(string file)
    {
        var name = Path.GetFileName(file);
        var properties = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? Read(string value, string element)
        {
            foreach (Match reference in PropertyReference().Matches(value))
            {
                properties.Add(reference.Groups[1].Value);
            }

            return UnrecordedReads.Any(read => value.Contains(read, StringComparison.OrdinalIgnoreCase))
                && !(name == "Microsoft.Managed.Core.targets" && element == "EditorConfigFiles")
                && !(name == "Microsoft.NET.Sdk.DefaultItems.targets" && element == "EnableDefaultItems")
                    ? $"{file} reads the machine in <{element}> in a way that is not followed"
                    : null;
        }

        try
        {
            using var reader = XmlReader.Create(file, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, IgnoreComments = true });
            var element = string.Empty;
            var inTarget = false;
            while (reader.Read())
            {
                // A target's body is read when it runs, which is the build's business and not the evaluation's. Its
                // BeforeTargets and AfterTargets are not: the evaluation expands them to order the targets.
                if (reader.Depth == 1 && reader.LocalName == "Target" && reader.NodeType is XmlNodeType.Element or XmlNodeType.EndElement)
                {
                    inTarget = reader.NodeType == XmlNodeType.Element && !reader.IsEmptyElement;
                    foreach (var attribute in new[] { "BeforeTargets", "AfterTargets" })
                    {
                        if (reader.NodeType == XmlNodeType.Element && reader.GetAttribute(attribute) is { } targets && Read(targets, "Target") is { } read)
                        {
                            return new ImportScan(read, properties);
                        }
                    }

                    continue;
                }

                if (inTarget)
                {
                    continue;
                }

                if (reader.NodeType == XmlNodeType.Element)
                {
                    element = reader.LocalName;
                    for (var hasAttribute = reader.MoveToFirstAttribute(); hasAttribute; hasAttribute = reader.MoveToNextAttribute())
                    {
                        if (Read(reader.Value, element) is { } read)
                        {
                            return new ImportScan(read, properties);
                        }
                    }
                }
                else if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA && Read(reader.Value, element) is { } read)
                {
                    return new ImportScan(read, properties);
                }
            }

            return new ImportScan(null, properties);
        }
        catch (Exception e) when (e is XmlException or IOException or UnauthorizedAccessException)
        {
            return new ImportScan($"{file} could not be read: {e.Message}", properties);
        }
    }

    // $(Name), $(Name.Length), $(Name::...), and with spaces after the parenthesis. Not $([Type]::Member).
    [GeneratedRegex(@"\$\(\s*([A-Za-z_][A-Za-z0-9_\-]*)")]
    private static partial Regex PropertyReference();
}

/// <summary>MSBuild's evaluation asks the disk through this, so every question it asks is written down.</summary>
internal sealed class RecordingFileSystem(Questions questions) : MSBuildFileSystemBase
{
    public override TextReader ReadFile(string path)
    {
        questions.AddFile(path);
        return base.ReadFile(path);
    }

    public override Stream GetFileStream(string path, FileMode mode, FileAccess access, FileShare share)
    {
        questions.AddFile(path);
        return base.GetFileStream(path, mode, access, share);
    }

    public override string ReadFileAllText(string path)
    {
        questions.AddFile(path);
        return base.ReadFileAllText(path);
    }

    public override byte[] ReadFileAllBytes(string path)
    {
        questions.AddFile(path);
        return base.ReadFileAllBytes(path);
    }

    public override IEnumerable<string> EnumerateFiles(string path, string searchPattern = "*", SearchOption searchOption = SearchOption.TopDirectoryOnly)
    {
        questions.AddList(Questions.Files, path, searchPattern, searchOption == SearchOption.AllDirectories);
        return base.EnumerateFiles(path, searchPattern, searchOption);
    }

    public override IEnumerable<string> EnumerateDirectories(string path, string searchPattern = "*", SearchOption searchOption = SearchOption.TopDirectoryOnly)
    {
        questions.AddList(Questions.Directories, path, searchPattern, searchOption == SearchOption.AllDirectories);
        return base.EnumerateDirectories(path, searchPattern, searchOption);
    }

    public override IEnumerable<string> EnumerateFileSystemEntries(string path, string searchPattern = "*", SearchOption searchOption = SearchOption.TopDirectoryOnly)
    {
        questions.AddList(Questions.Either, path, searchPattern, searchOption == SearchOption.AllDirectories);
        return base.EnumerateFileSystemEntries(path, searchPattern, searchOption);
    }

    // What the attributes or the time of a file say, its content and its being there say too.
    public override FileAttributes GetAttributes(string path)
    {
        questions.AddExists(Questions.Either, path);
        questions.AddFile(path);
        return base.GetAttributes(path);
    }

    public override DateTime GetLastWriteTimeUtc(string path)
    {
        questions.AddFile(path);
        return base.GetLastWriteTimeUtc(path);
    }

    public override bool DirectoryExists(string path)
    {
        var exists = base.DirectoryExists(path);
        questions.AddExists(Questions.Directories, path, exists);
        return exists;
    }

    public override bool FileExists(string path)
    {
        var exists = base.FileExists(path);
        questions.AddExists(Questions.Files, path, exists);
        return exists;
    }

    public override bool FileOrDirectoryExists(string path)
    {
        var exists = base.FileOrDirectoryExists(path);
        questions.AddExists(Questions.Either, path, exists);
        return exists;
    }
}

/// <summary>MSBuild says which environment variables an evaluation read, and only says so to a logger.</summary>
internal sealed class EnvironmentLogger(Questions questions) : ILogger
{
    public LoggerVerbosity Verbosity { get; set; } = LoggerVerbosity.Diagnostic;

    public string? Parameters { get; set; }

    public void Initialize(IEventSource eventSource)
    {
        ArgumentNullException.ThrowIfNull(eventSource);
        eventSource.AnyEventRaised += (_, e) =>
        {
            if (e is EnvironmentVariableReadEventArgs read)
            {
                questions.AddEnvironment(read.EnvironmentVariableName);
            }
        };
    }

    public void Shutdown()
    {
    }
}
