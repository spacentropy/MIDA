# Affected-project detection, incremental builds, and build/test caching for .NET monorepos (as of Oct 2026)

Source-quality note: the source for each claim is linked. "Fetched" means the page was retrieved in this session (Oct 28 2026). A few stable facts are cited to canonical Microsoft Learn or GitHub doc URLs that were *not* re-fetched this session. Those are marked "(canonical doc, not re-fetched)" and should be spot-checked before they go in the final report.

---

## 1. Affected detection: how tools map changed files to projects

### Takeaway
All mature tools work the same way: (1) git-diff against a base ref, (2) map each changed file to the projects that "own" it, (3) take the reverse-transitive closure over the `ProjectReference` graph.

They differ on step 2:
- **dotnet-affected** and **Nx @nx/dotnet** use real MSBuild evaluation. That lets them catch imports, `Directory.*.props/targets`, linked files outside the project directory, and per-package CPM changes.
- **Incrementalist** and naive scripts fall back to "rebuild everything" for solution-wide files, or to folder containment.

The hardest remaining gaps are non-MSBuild-visible inputs:
- files read at runtime by tests
- analyzer/source-generator `AdditionalFiles` declared only at build time
- SDK and global.json changes
- NuGet package content changes that leave the version unchanged

### Cited Findings

**dotnet-affected (leonardochaia/dotnet-affected), fetched**
- Compares the working tree against a baseline commit. The baseline can be a branch, the last successful CI commit, or `HEAD` by default. It maps changed files to projects, then walks the MSBuild project graph for dependents. — [dotnet-affected README](https://github.com/leonardochaia/dotnet-affected); [dotnet-affected.com](https://dotnet-affected.com/)
- The README says it detects changes in "any file they reference, not just source files", including `Directory.Build.props`/`.targets` and other imported files. — [README](https://github.com/leonardochaia/dotnet-affected)
- Uses MSBuild `ProjectGraph` to find all transitively affected projects. — [dotnet-affected.com](https://dotnet-affected.com/)
- With Central Package Management it "detects added, removed, and updated packages in `Directory.Packages.props`". Projects referencing a changed package become changed, rather than every project that imports the props file. — [dotnet-affected.com](https://dotnet-affected.com/)
- Described pipeline: discover all project files → git diff → map changed files to projects → detect `Directory.Packages.props` package changes → feed changed projects and packages into `ProjectGraph` → compute transitive affected. — summarized via [search result aggregating docs](https://docsearch.algolia.com/mcp/docs/repo/leonardochaia/dotnet-affected) (aggregator, medium confidence)
- Output formats: Traversal SDK project (`affected.proj`, usable with `dotnet build`/`dotnet test`), Solution Filter (`.slnf`), plain text, JSON. Example: `dotnet affected --from origin/main --uncommitted none`. If nothing changed it writes no file and exits with code **166**, so CI can skip later steps. — [README](https://github.com/leonardochaia/dotnet-affected)
- Supports `.csproj`, `.fsproj`, `.vbproj`, both SDK-style and legacy. Also ships an MSBuild SDK (`DotnetAffected.Tasks`) for running inside MSBuild without the CLI, plus a GitHub Action. — [README](https://github.com/leonardochaia/dotnet-affected); [NuGet DotnetAffected.Tasks](https://packages.nuget.org/packages/DotnetAffected.Tasks)
- **v7.0.0** is "6.5× faster than v6.2.0" on a 2,000-project repository, and the gap grows with repo size. There is a v6→v7 upgrade guide. — [dotnet-affected.com](https://dotnet-affected.com/)
- Repo stats at fetch time: 413 stars, 156 commits, 8 open issues. — [README](https://github.com/leonardochaia/dotnet-affected)
- **Historical gap (older, date unclear, likely 2022–2023):** an issue reported that `Directory.Build.props` changes were not detected. A separate issue reported "incorrect evaluation of Directory.Packages.props", and an issue on 3.0.0-preview-4 reported .NET 7 partial-support errors. The current README claims imported-file detection, so treat these as fixed in later versions. — [lightrun mirror: Directory.Build.props not detected](https://lightrun.com/answers/leonardochaia-dotnet-affected-changes-in-directorybuildprops-not-detected); [lightrun: incorrect evaluation of Directory.Packages.props](https://lightrun.com/answers/leonardochaia-dotnet-affected-incorrect-evaluation-of-directorypackagesprops); [lightrun: .NET 7 partial support](https://lightrun.com/answers/leonardochaia-dotnet-affected-dotnet-7-partial-support)

**Nx @nx/dotnet (official Nx plugin), fetched**
- Available since **Nx 22.0.0**. Requires .NET SDK 8.0+; older SDKs are unsupported. Set up with `nx init` or `nx add @nx/dotnet`. It replaces the community `@nx-dotnet/core` and has a migration guide. — [Nx @nx/dotnet intro (canary)](https://canary.nx.dev/docs/technologies/dotnet/introduction); [Migrate from @nx-dotnet/core](https://nx.dev/docs/technologies/dotnet/guides/migrate-from-nx-dotnet-core)
- Detection: globs `**/*.csproj`, `**/*.fsproj`, `**/*.vbproj`. It uses MSBuild to read `<ProjectReference>` (graph edges), available targets, outputs and configuration. — [Nx intro](https://canary.nx.dev/docs/technologies/dotnet/introduction)
- The cache hash for each target covers:
  - the project's own files
  - outputs of the projects it depends on
  - files MSBuild reads from outside the project directory during evaluation. Specifically:
    - the nearest `Directory.Build.props`, `Directory.Build.targets`, `Directory.Build.rsp`, `Directory.Solution.props`, `Directory.Solution.targets`, `Directory.Packages.props` and `global.json` above the project
    - **every** `nuget.config` and `.editorconfig` between the project and the workspace root
    - other imported files
    - `Compile`, `AdditionalFiles`, `EmbeddedResource` or `Content` items linked from other directories
    - the `NUGET_PACKAGES` environment variable

  Editing these files updates the graph without `nx reset`. — [Nx intro](https://canary.nx.dev/docs/technologies/dotnet/introduction)
- Inferred targets: `build`, `clean`, `publish`, `pack`, `run`, `watch`, `test`, and `restore` (opt-in). Test projects are identified by `IsTestProject` / `IsTestingPlatformApplication`. Before restore those properties are empty, so Nx falls back to looking for `Microsoft.NET.Test.Sdk` / `Microsoft.Testing.*` package references, and you must run `nx reset` after the first restore. — [Nx intro](https://canary.nx.dev/docs/technologies/dotnet/introduction)
- Builds run with `--no-restore` and `--no-dependencies`. Nx adds `^build` so it handles ordering itself. Restore's top-level `obj` files are excluded from build/publish/pack outputs, so a cache hit does not replace restore. An unrestored project fails with `NETSDK1004`. — [Nx intro](https://canary.nx.dev/docs/technologies/dotnet/introduction); [Nx search summary of incremental-builds guide](https://nx.dev/docs/technologies/dotnet/guides/incremental-builds) (that URL returned 404 on direct fetch; the content came from a search snippet)
- `dotnet test` writes no results unless a logger or collector is configured. To cache test results you must pass `results-directory` plus a logger, and the target then declares `{projectRoot}/{options.results-directory}` as output. — [Nx intro](https://canary.nx.dev/docs/technologies/dotnet/introduction)
- Affected: `nx affected` uses the same `ProjectReference`-derived graph. The intro page does not document affected internals beyond that. — [Nx intro](https://canary.nx.dev/docs/technologies/dotnet/introduction)

**Incrementalist (petabridge/Incrementalist), fetched**
- "Leverages libgit2sharp and Roslyn". Diffs against a target branch (`-b`, e.g. `dev`/`master`). — [Incrementalist README](https://github.com/petabridge/Incrementalist)
- Algorithm per how-it-works.md:
  1. Collect git changes.
  2. If any change is "solution-wide", build everything. The named triggers are `Directory.Build.props`, `global.json` and `.sln` files.
  3. Otherwise run project analysis, which finds dependencies and builds the list.

  `Directory.Packages.props` is **not mentioned**. — [Incrementalist how-it-works.md](https://raw.githubusercontent.com/petabridge/Incrementalist/dev/docs/how-it-works.md)
- CLI verbs: `run` / `run-process` (runs `dotnet` commands against affected projects; supports `--parallel`, `--parallel-limit`, `--dry`, `--continue-on-error`, `--fail-on-no-projects`), `list-affected-folders`, `create-config`. Options include `--skip-glob` and `--target-glob`, both applied *after* dependency analysis. `-s/--sln` selects the solution, defaulting to the first `.sln` in the current directory. `-t` is the solution-load timeout (default 2 min). Requires the .NET 8 SDK+ and git on PATH. — [README](https://github.com/petabridge/Incrementalist)
- Output is a comma-separated list of project paths or folder paths. — [README](https://github.com/petabridge/Incrementalist)

**Datadog Test Impact Analysis notes on non-code inputs (relevant to affected detection gaps)**
- Coverage-based selection can miss tests that depend on non-code inputs such as text files read at runtime or external REST APIs. — [Datadog TIA how it works](https://docs.datadoghq.com/tests/test_impact_analysis/how_it_works/)

### Inferences
- **Mapping changed files to projects.** There are three levels of fidelity:
  - (a) path containment: is the file under the project directory? Naive scripts use this, and it is wrong for linked files and for files excluded by globs.
  - (b) static XML parse.
  - (c) full MSBuild evaluation: enumerate `Compile`, `EmbeddedResource`, `None`, `Content`, `AdditionalFiles` items after globbing, plus `Project.Imports` for imported props/targets.

  Only (c) catches `<Compile Include="..\..\Shared\Foo.cs" Link=...>` and glob exclusions such as `<Compile Remove="Legacy/**" />`. dotnet-affected and Nx both use (c). A new tool should use evaluation items **plus** `ProjectInstance`/`Project.Imports` to attribute imported files.
- **Directory.Build.props/targets.** The precise approach is to attribute the file to every project that imports it, which is evaluation-based. The coarse approach is "rebuild all" (Incrementalist). Nested `Directory.Build.props` (one per subtree) makes the precise approach much better in practice.
- **Directory.Packages.props (CPM).** The precise approach:
  1. Diff the old and new `PackageVersion` items. This needs evaluation at both the base and head commit, or an XML diff.
  2. Mark projects whose `PackageReference` (or `GlobalPackageReference`) includes a changed package.
  3. With `CentralPackageTransitivePinningEnabled`, a changed pin can affect projects that consume that package only *transitively*. This requires reading `project.assets.json` or `packages.lock.json` per project to map package→project. The dotnet-affected docs found here do not say whether transitive pins are handled, so treat it as a likely gap.
- **packages.lock.json.** This is a per-project file under the project directory, so containment-based mapping already works. However, a lock-file change caused only by restore with a different SDK (implicit references) would mark the project as changed.
- **global.json / SDK version.** This affects every project: the SDK's implicit package references, analyzers and compiler version all change. A correct tool should treat it as a universal input. Nx hashes it, and Incrementalist triggers a full build on it.
- **Source generators and analyzers.** A `ProjectReference` with `OutputItemType="Analyzer" ReferenceOutputAssembly="false"` is still a `ProjectReference` edge, so graph-based tools pick it up. Generator inputs that come via `AdditionalFiles` are only visible if the tool evaluates items, which Nx explicitly includes. Generators that read arbitrary files are invisible to every evaluation-based tool.
- **Embedded resources and `.resx`.** Visible as `EmbeddedResource` items under evaluation. Path containment also works if they live in the project directory.
- **Deleted files.** A diff-based tool must evaluate the *base* commit too, or keep a cached file→project map. Otherwise a deleted linked file cannot be attributed to its project. Neither tool's docs found here explain deletion handling. That is a gap.
- **Multi-targeting.** `ProjectGraph` contains outer and inner build nodes (see section 2). Affected tools should collapse them to the project file level.

### Gaps
- Could not fetch dotnet-affected's detailed "how it works", NuGet-changes and CLI reference pages; `/docs/how-it-works` and `/docs/cli-reference` both returned 404. The exact v7 CLI flags (`--from`, `--to`, `--assume-changes`, `--exclude`, `--filter-file-path`, `--format`) and their semantics are therefore unverified for v7.
- Did not verify whether dotnet-affected handles CPM transitive pinning, `VersionOverride`, or `GlobalPackageReference`.
- Did not verify whether Incrementalist uses MSBuildWorkspace or Roslyn `Solution` loading for its dependency analysis; the README only says "libgit2sharp and Roslyn". Its latest version number and release date were not found.
- Nx's affected logic for .NET at the file level is not documented beyond the graph and hash inputs. Did not find Nx issues reporting missed .NET changes.
- Found no good public write-ups on custom git-diff scripts; their behaviour is inferred above.

---

## 2. MSBuild evaluation and project graph APIs

### Takeaway
`Microsoft.Build.Graph.ProjectGraph` is the canonical API. Nodes are (project, global properties) pairs evaluated in parallel, and edges come from `ProjectReference`. Its static-graph semantics (`-graph`, `-isolate`, `ProjectReferenceTargets`) are also the foundation for MSBuild caching.

For lightweight queries, use `dotnet msbuild -getProperty/-getItem/-getTargetResult` (MSBuild 17.8+, i.e. .NET 8 SDK). These give JSON without custom hosting.

Evaluation cost scales with the number of projects × imports. Multi-targeting multiplies the node count.

### Cited Findings
- **Nodes and edges.**
  - Static graph nodes are (project file, global properties) pairs and can be evaluated in parallel. Edges come from `ProjectReference`.
  - Transitive references are opt-in per project via `AddTransitiveProjectReferencesInStaticGraph`.
  - A direct and an implied transitive reference to the same node collapse into one edge, with targets merged and deduplicated.
  - Cycles are disallowed, even across disconnected targets.
  - Public API: `ProjectGraph`, `ProjectGraphEntryPoint`, `ProjectGraphNode`.

  — [MSBuild static-graph spec](https://github.com/dotnet/msbuild/blob/main/documentation/specs/static-graph.md)
- **Multi-targeting.**
  - An outer build is identified by an empty `InnerBuildProperty` with `InnerBuildPropertyValues` set. Inner builds carry `TargetFramework`.
  - A referencing project gets *speculative* edges to all inner builds.
  - An open issue: dependent and standalone inner builds aren't distinguished yet, which can cause overbuilding.

  — [static-graph spec](https://github.com/dotnet/msbuild/blob/main/documentation/specs/static-graph.md)
- **ProjectReferenceTargets protocol.** Each project declares which targets an incoming target calls on its references, e.g. `Build -> GetTargetFrameworks, .default, GetNativeManifest, GetCopyToOutputDirectoryItems`. `OuterBuild=true` metadata marks outer-only targets. — [static-graph spec](https://github.com/dotnet/msbuild/blob/main/documentation/specs/static-graph.md)
- **`-graph` vs `-graph -isolate`.**
  - `-graph` schedules projects in dependency order (references finish before referencers). Underspecified references silently fall back to just-in-time execution.
  - `-graph -isolate` makes any `MSBuild` task call the graph didn't predict an error, as well as calls to projects that are not declared as `ProjectReference`.
  - Exemptions: the `GraphIsolationExemptReference` item, or `isolate:MessageUponIsolationViolation`.
  - `/inputResultsCaches` and `/outputResultsCache` serialize *build results* (not outputs) between BeginBuild and EndBuild sessions, and imply isolation. This is the basis for distributed or "cloudbuild"-style per-project builds.

  — [static-graph spec](https://github.com/dotnet/msbuild/blob/main/documentation/specs/static-graph.md)
- **Static-graph limitations.**
  - The graph is inferred from evaluation-time XML, not from task call sites, so it is a conservative superset of what actually runs.
  - Evaluation-time I/O tracking is "on hold".
  - Tool servers (the Roslyn compiler server) can't be tracked by detouring the MSBuild process, and analyzers can perform arbitrary I/O.
  - Restore "doesn't map cleanly" onto the protocol.
  - Multi-targeting rules cover `Microsoft.NET.Sdk` but may need extension for other SDKs.

  — [static-graph spec](https://github.com/dotnet/msbuild/blob/main/documentation/specs/static-graph.md)
- **`-getProperty:{a,b}`, `-getItem:{X}`, `-getTargetResult:{T}`** are available in **MSBuild 17.8+** and work with `msbuild` and `dotnet build/publish/msbuild`.
  - Without `-target`, the values come from evaluation only and no targets run.
  - With `-target` or `-getTargetResult`, the values are those after the build.
  - A single property prints as plain text; anything more prints JSON grouped as `Properties`, `Items` (with full metadata, including `DefiningProjectFullPath`) and `TargetResults`.
  - Caveat: properties are evaluated before items, so a property that references items differs between evaluation-only and post-target queries.
  - Doc updated 2026-10-20. — [MS Learn: Evaluate items and properties](https://learn.microsoft.com/en-us/visualstudio/msbuild/evaluate-items-and-properties)
- **Item metadata for attribution.** Each item from `-getItem:Compile` carries `FullPath` and `DefiningProjectFullPath`, which shows which `.props`/`.targets` file added it. Generated items in `obj/` (`GlobalUsings.g.cs`, `AssemblyAttributes.cs`, `AssemblyInfo.cs`) are part of the `Compile` item set, and `Build` target results include a `ReferenceAssembly` path in `obj/.../ref/`. — [MS Learn: Evaluate items and properties](https://learn.microsoft.com/en-us/visualstudio/msbuild/evaluate-items-and-properties)
- **Microsoft.Build.Locator.** `MSBuildLocator.RegisterDefaults()` (or `RegisterInstance`) must run before any `Microsoft.Build` types are loaded. Microsoft.Build packages must be referenced with `ExcludeAssets="runtime"` so the SDK's copies load. (canonical doc, not re-fetched) — [MS Learn: Find and use MSBuild versions](https://learn.microsoft.com/en-us/visualstudio/msbuild/find-and-use-msbuild-versions)
- **Buildalyzer.** It runs a design-time build with a custom logger (binary-log based) to capture `Csc` command-line arguments, source files and references. It is more complete than evaluation (it includes target-computed items) but much slower, because it executes targets. (canonical doc, not re-fetched) — [Buildalyzer GitHub](https://github.com/phmonte/Buildalyzer)

### Inferences
- **Recommended path for a new tool.** Host `ProjectGraph` in-process via MSBuildLocator. Inside a `ProjectCollection`, share an `EvaluationContext` (`EvaluationContext.Create(SharingPolicy.Shared)`), which caches file-system globbing and imports across evaluations. Collapse inner and outer nodes to the file level. Use `ProjectInstance.Items` and `Project.Imports` to build a file→project attribution map.
- `-getProperty/-getItem` is convenient for scripts, but it spawns one process per project unless pointed at a traversal or solution. A long-lived host process is better at 1,000+ projects.
- dotnet-affected's 6.5× v7 speedup on 2,000 projects suggests evaluation and graph construction are the bottleneck at scale. Caching evaluation results keyed on hashes of the imported files is a key optimization.
- Speculative edges to all inner builds mean that changing one TFM-specific file may mark all TFMs as affected.

### Gaps
- Found no published benchmark numbers for raw `ProjectGraph` evaluation time per project in large repos, beyond dotnet-affected's relative figure.
- Did not verify whether MSBuild in .NET 10 changed the default for `-graph` or introduced new evaluation caching.

---

## 3. MSBuild incrementality and why `dotnet build` is slow in large repos

### Takeaway
MSBuild incrementality is per-target timestamp comparison of `Inputs`/`Outputs`, with no content hashing. A no-op `dotnet build` still pays for evaluation, restore checks, and a walk of every project's target graph.

Reference assemblies (`ProduceReferenceAssembly`, on by default in modern SDKs) prevent downstream recompilation when a dependency changes only its implementation. Copying of outputs still happens, though.

The main scale problems:
- the evaluation cost
- the implicit restore inside `dotnet build`
- ProjectReference re-traversal (each project re-invokes `Build`/`GetTargetPath` on its references)
- MSBuild's lack of content-addressed caching

### Cited Findings
- **Reference assemblies in practice.** The `Build` target result for a project includes `ReferenceAssembly = obj\Debug\<tfm>\ref\<name>.dll`, which shows the SDK produces ref assemblies by default and passes them to referencers. — [MS Learn: Evaluate items and properties (example output)](https://learn.microsoft.com/en-us/visualstudio/msbuild/evaluate-items-and-properties)
- **Incremental targets.** MSBuild skips a target when all `Outputs` are newer than all `Inputs` (timestamp-based). When it can map inputs to outputs 1:1, it does partial builds. (canonical doc, not re-fetched) — [MS Learn: Incremental builds](https://learn.microsoft.com/en-us/visualstudio/msbuild/incremental-builds)
- **Reference assemblies and CopyRefAssembly.** `ProduceReferenceAssembly` defaults to `true` for .NET 5+ SDK projects. Only the public API surface goes into `obj/ref/`, and the `CopyRefAssembly` task doesn't touch the ref copy if its content is unchanged. Downstream `CoreCompile` therefore sees unchanged inputs and is skipped when only private implementation changed. (canonical doc, not re-fetched) — [MS Learn: Reference assemblies](https://learn.microsoft.com/en-us/dotnet/standard/assembly/reference-assemblies)
- **VS fast up-to-date check.** This VS project-system shortcut avoids calling MSBuild. It is not used by the CLI. (canonical doc, not re-fetched) — [dotnet/project-system up-to-date-check.md](https://github.com/dotnet/project-system/blob/main/docs/up-to-date-check.md)
- **Compiler server.** The Roslyn compiler server (`VBCSCompiler`) is reused via `UseSharedCompilation`. The static-graph spec notes that tool servers like it can't be tracked by detouring MSBuild, which is why MSBuildCache ships a `SharedCompilation` package that reports compiler file accesses manually. — [static-graph spec](https://github.com/dotnet/msbuild/blob/main/documentation/specs/static-graph.md); [MSBuildCache README](https://github.com/microsoft/MSBuildCache)
- **Nx build flags.** Nx runs `dotnet build --no-restore --no-dependencies` (the CLI form of `-p:BuildProjectReferences=false`) and orders builds itself via `^build`. — [Nx intro](https://canary.nx.dev/docs/technologies/dotnet/introduction)
- **Graph scheduling.** `-graph` keeps cores busy because no build pauses waiting for references. In isolated mode, project state can be released early, reducing memory on large builds. — [static-graph spec](https://github.com/dotnet/msbuild/blob/main/documentation/specs/static-graph.md)
- **Deterministic builds.** `Deterministic=true` is the SDK default. `ContinuousIntegrationBuild=true` normalizes source paths in PDBs and the binary (via PathMap-style mapping to `/_/`) and is recommended only for CI. (canonical doc, not re-fetched) — [MS Learn: MSBuild props, ContinuousIntegrationBuild](https://learn.microsoft.com/en-us/dotnet/core/project-sdk/msbuild-props#continuousintegrationbuild); [C# compiler option PathMap](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/compiler-options/advanced#pathmap)

### Inferences
- Why `dotnet build` is slow in big repos:
  - Implicit restore runs on every build unless `--no-restore` is passed.
  - Every project is evaluated, including SDK imports, often 100+ files per project.
  - Each project's `ResolveProjectReferences` re-enters referenced projects, which costs cache lookups but is still expensive.
  - Timestamp checks on `CopyFilesToOutputDirectory` scale with transitive dependency counts.
  - Multi-targeting multiplies everything.

  The standard mitigations:
  - restore once, then use `--no-restore`
  - a build tool orders projects and passes `-p:BuildProjectReferences=false`
  - `-graph`
  - node reuse (`-nr:true`, the default locally)
  - reference assemblies
  - avoiding `CopyLocal` of large transitive dependency trees
- **Timestamps break under caching.** MSBuild timestamps are invalidated by git checkout and by cache restores, which write new mtimes. A caching layer must either restore outputs with preserved timestamps or skip MSBuild entirely on a hit. MSBuildCache has `MSBuildCacheTouchOutputFiles` and `MSBuildCacheSkipUnchangedOutputFiles` for this.
- **Ref assemblies and hashing.** A cache key should hash the *ref assembly* of each dependency, not the implementation DLL. That reproduces the "implementation-only change doesn't invalidate dependents' compile" benefit. A test or run step still depends on the implementation DLL.

### Gaps
- No fresh 2026 benchmarks were found on `dotnet build` no-op times at scale.
- Did not verify the .NET 9/10 status of the MSBuild server (`DOTNET_CLI_USE_MSBUILD_SERVER`, opt-in since .NET 7).
- Did not verify whether the terminal logger (default in .NET 9) affects performance.

---

## 4. Caching: MSBuildCache, the project cache plugin API, BuildXL, Bazel, Nx Cloud

### Takeaway
Microsoft's MSBuildCache is the only native MSBuild output cache.
- **Mechanism:** the project cache plugin API plus Detours-based file-access reporting.
- **Backends:** a BuildXL-based local cache, Azure Blob, and Azure Pipelines caching.
- **Constraints:** it needs `msbuild /graph /m /reportfileaccesses`. File-access reporting is limited to MSBuild.exe (VS install), x64, command-line only. In effect that means Windows. The API is still labelled experimental.

Nx caches at the task level (whole `dotnet build` invocations per project), with hashes built from MSBuild-reported external inputs.

Bazel's rules_dotnet bypasses MSBuild entirely.

What makes .NET outputs hard to cache:
- absolute paths in outputs and serialized results
- `obj/` intermediate state (assets files, caches)
- timestamps driving MSBuild incrementality
- detached processes such as the compiler server
- non-deterministic generators and tasks

### Cited Findings

**MSBuild project cache plugin API (dotnet/msbuild spec), fetched**
- An assembly-based plugin decides whether a top-level build request can be skipped. Key methods:
  - `BeginBuildAsync`: fire-and-forget; receives a `ProjectGraph` or entry points and `CacheContext`.
  - `GetCacheResultAsync`: the cache get.
  - `HandleFileAccess` / `HandleProcess`.
  - `HandleProjectFinishedAsync`: the cache add, typically only for successful builds.
  - `EndBuildAsync`: blocking.

  — [MSBuild project-cache spec](https://github.com/dotnet/msbuild/blob/main/documentation/specs/project-cache.md)
- **Configuration.** Plugins are declared with `ProjectCachePlugin` items, injectable via `PackageReference`, or programmatically via `BuildParameters.ProjectCacheDescriptor`. Settings arrive as item metadata in `CacheContext.PluginSettings`. — [project-cache spec](https://github.com/dotnet/msbuild/blob/main/documentation/specs/project-cache.md)
- **Execution model.** Only top-level requests are checked, and they are assumed to arrive in reverse topological order. Plugin instances live only in the BuildManager node. — [project-cache spec](https://github.com/dotnet/msbuild/blob/main/documentation/specs/project-cache.md)
- **Command line.** `/graph` is required for cache get, and `/reportfileaccesses` is required for cache add. — [project-cache spec](https://github.com/dotnet/msbuild/blob/main/documentation/specs/project-cache.md)
- **Three ways to satisfy a hit:**
  - (1) fabricate target results (brittle)
  - (2) proxy targets: cheap targets like `GetTargetPath` run in place of `Build`; they must be fast, side-effect-free, and must not call other projects
  - (3) a serialized real `BuildResult`, which is most correct but has serialization-compatibility risk

  — [project-cache spec](https://github.com/dotnet/msbuild/blob/main/documentation/specs/project-cache.md)
- **File-access reporting.** It uses Detours on worker nodes, disables the in-proc node and task yielding for attribution, and costs roughly **10–15% overhead**. It is limited to **MSBuild.exe (VS install), x64 only, command-line only**. — [project-cache spec](https://github.com/dotnet/msbuild/blob/main/documentation/specs/project-cache.md)
- **Visual Studio.** VS supplies no graph, only entry points. Cache add is unavailable in VS. — [project-cache spec](https://github.com/dotnet/msbuild/blob/main/documentation/specs/project-cache.md)
- **Stated limitations:**
  - absolute paths in saved results can break builds on other machines
  - slow networks can make downloading slower than building
  - `/graph` depends on the target inference protocol
  - small repos may get slower
  - per-target skipping is only future work

  — [project-cache spec](https://github.com/dotnet/msbuild/blob/main/documentation/specs/project-cache.md)
- **History.** Cache get shipped in **MSBuild 16.9** and cache add in **MSBuild 17.8**. File-access reporting is described as experimental. — [project-cache spec](https://github.com/dotnet/msbuild/blob/main/documentation/specs/project-cache.md)

**microsoft/MSBuildCache, fetched**
- **Packages:**
  - `Microsoft.MSBuildCache.Local`: BuildXL `LocalCache`. Default root is `\MSBuildCache` on the repo drive, with a 100 GB default limit.
  - `Microsoft.MSBuildCache.AzureBlobStorage`. Auth order: config file → 1ES Build Cache resource ID → `MSBCACHE_CONNECTIONSTRING` → blob URI with Azure credentials. Interactive auth is on except in Azure Pipelines or GitHub Actions.
  - `Microsoft.MSBuildCache.AzurePipelines`: needs `SYSTEM_ACCESSTOKEN`.
  - `Microsoft.MSBuildCache.SharedCompilation`: needed for C#, to report `VBCSCompiler` file accesses.

  — [MSBuildCache README](https://github.com/microsoft/MSBuildCache)
- **Requirements.** VS **17.9+**. Run `msbuild /graph /m /reportfileaccesses`. Add a backend via `PackageReference` or `GlobalPackageReference`. To cache tests, add `/t:Build;Test` with a test-hooked target such as `Microsoft.Build.RunVSTest`. — [MSBuildCache README](https://github.com/microsoft/MSBuildCache)
- **Fingerprinting inputs:**
  - global properties, minus `MSBuildCacheGlobalPropertiesToIgnore`
  - targets, with `MSBuildCacheTargetsToIgnore` such as `GetTargetFrameworks` ignored (one way only)
  - file probes and directory enumerations, through `MSBuildCacheEnableProbeAndEnumerationFingerprinting` (default true; forced off on MSBuild versions that don't report enumeration patterns)
  - `MSBuildCacheCacheUniverse` for isolating or busting the cache
  - `MSBuildCacheIgnoreDotNetSdkPatchVersion`

  — [MSBuildCache README](https://github.com/microsoft/MSBuildCache)
- **Input/output knobs:**
  - `MSBuildCacheIgnoredInputPatterns`
  - `MSBuildCacheIgnoredOutputPatterns`, default `*.assets.cache; *assemblyreference.cache`. Ignored outputs are *missing* on cache hits.
  - `MSBuildCacheIdenticalDuplicateOutputPatterns` (double writes)
  - late-file-access allowlists for detached processes
  - `MSBuildCacheSkipUnchangedOutputFiles` and `MSBuildCacheTouchOutputFiles`
  - `MSBuildCacheGetResultsForUnqueriedDependencies`, for builds not in graph order
  - `MSBuildCacheRemoteCacheIsReadOnly`
  - async publish and materialization

  — [MSBuildCache README](https://github.com/microsoft/MSBuildCache)
- **Security.** The Azure Blob plugin "does not yet have a robust security model". Any build with write access can poison the cache. — [MSBuildCache README](https://github.com/microsoft/MSBuildCache)

**Nx (task-level caching for .NET)**
- Hash inputs are listed in section 1. Notably, they include `.editorconfig` files (analyzers read them) and `NUGET_PACKAGES`. Outputs exclude restore's top-level `obj` files. — [Nx intro](https://canary.nx.dev/docs/technologies/dotnet/introduction)

**Bazel rules_dotnet**
- Positioned as "an alternative to using MSBuild with .Net" ("{Fast, Correct} - Choose two"). Distributed via the Bazel Central Registry (bzlmod `MODULE.bazel`). The repo uses Paket (`paket.dependencies`/`paket.lock`) for NuGet. Fetched stats: 211 stars, 30 open issues. — [rules_dotnet GitHub](https://github.com/bazel-contrib/rules_dotnet)

### Inferences
- **Why .NET outputs are hard to cache:**
  - Absolute paths are embedded in:
    - PDBs, unless `ContinuousIntegrationBuild`/`PathMap` is set
    - `project.assets.json` and `nuget.g.props` (the user's NuGet package root)
    - serialized `BuildResult`s, per the project-cache spec
    - `*.AssemblyReference.cache`
  - `obj/` holds restore outputs that a build must not overwrite (Nx excludes them) and incremental caches that MSBuildCache ignores by default.
  - Timestamps drive the next incremental build.
  - The compiler server and other out-of-proc tools escape file-access tracing.
  - Source generators and analyzers can read arbitrary files.
  - `GenerateAssemblyInfo`, SourceLink (git commit SHA embedded in the PDB and `AssemblyInformationalVersion`), and versioning tools (GitVersion, Nerdbank.GitVersioning) inject commit-dependent data. That changes outputs on every commit even when the code is unchanged. Recommended fix: exclude the version stamp from cache keys, or stamp it only at pack/publish.
- **Hashing design for a new tool.** A Gradle- or Nx-style key per project could combine:
  - hash(evaluated `Compile`, `EmbeddedResource`, `AdditionalFiles`, `Content` items)
  - hash(imported props/targets content)
  - hash(the resolved package set from `project.assets.json`, using package ID+version plus `.nupkg.sha512`)
  - hash(dependency ref assemblies)
  - SDK version from global.json or the actual SDK
  - relevant global properties (Configuration, RuntimeIdentifier)
  - `.editorconfig`/`globalconfig` files

  This avoids Detours, so it is cross-platform, at the cost of soundness when tasks read undeclared files.
- **MSBuildCache's Windows-only status.** File-access reporting requires the MSBuild.exe x64 Detours path, so MSBuildCache is effectively Windows-only. That makes it a niche choice for Linux/macOS CI. This is an inference from the spec's limitation; verify the current state for `dotnet build` on Linux.
- **Bazel / rules_dotnet.** You get hermetic remote caching and execution, but you give up MSBuild extensibility: `.csproj` is no longer the source of truth, IDE integration is harder, and NuGet goes through Paket or Bazel repository rules.

### Gaps
- Did not fetch BuildXL documentation (the MSBuild frontend/resolver, sandboxing, cache), Nx Cloud .NET-specific docs, or rules_dotnet docs on remote caching, source generators and compilation (csc direct invocation). These need verification.
- Did not confirm whether MSBuildCache now works with `dotnet build` (the .NET SDK MSBuild) on Linux or macOS in 2026, or its latest version number.
- No source was found on "Gradle-style hashing" implementations for .NET beyond Nx.

---

## 5. Test selection, test impact analysis, and sharding

### Takeaway
Affected-project selection (graph-based) is the cheapest and safest layer: run test projects whose closure changed. Finer-grained selection needs per-test coverage (Datadog TIA, Azure DevOps TIA).

.NET 10 introduced a native Microsoft.Testing.Platform (MTP) mode for `dotnet test`, opted into via `global.json`. This changes how test projects are detected and run. A solution in MTP mode cannot mix in VSTest projects.

### Cited Findings
- **Two modes, chosen in global.json.**
  - VSTest is the default for `dotnet test` and was the only mode before the .NET 10 SDK.
  - MTP mode arrived with the .NET 10 SDK. Enable it with `{"test": {"runner": "Microsoft.Testing.Platform"}}` in `global.json`, which removes the need for `TestingPlatformDotnetTestSupport` and the extra `--`. It requires MTP **1.7.0+**.

  — [MS Learn: Unit testing with dotnet test](https://learn.microsoft.com/dotnet/core/testing/unit-testing-with-dotnet-test); [.NET blog: dotnet test with MTP (Aug 21 2025)](https://devblogs.microsoft.com/dotnet/dotnet-test-with-mtp/); [MS Learn: dotnet test (MTP)](https://learn.microsoft.com/ar-sa/dotnet/core/tools/dotnet-test-mtp)
- **No mixing.** In MTP mode, every test project in the solution must use MTP. VSTest projects can't run. — [MS Learn: Unit testing with dotnet test](https://learn.microsoft.com/dotnet/core/testing/unit-testing-with-dotnet-test)
- **CI pitfall (third-party, Oct 2026).** `dotnet test` silently falls back to VSTest when `global.json` isn't found from the working directory, or when the property name is miscased (e.g. `"Test"`). — [startdebugging.net (Oct 2026)](https://startdebugging.net/de/2026/10/fix-dotnet-test-falls-back-to-vstest-in-linux-ci-microsoft-testing-platform/)
- **Unconfirmed.** A community post claims a `DOTNET_TEST_RUNNER` env var overrides global.json in .NET 11 Preview 6+. Not confirmed in Microsoft docs. — [startdebugging.net](https://startdebugging.net/de/2026/10/fix-dotnet-test-falls-back-to-vstest-in-linux-ci-microsoft-testing-platform/)
- **Nx test-project detection.** Nx detects test projects via `IsTestProject` / `IsTestingPlatformApplication`, the same properties the SDK checks. — [Nx intro](https://canary.nx.dev/docs/technologies/dotnet/introduction)
- **Datadog Test Impact Analysis** (formerly Intelligent Test Runner):
  - collects per-test (or per-suite) coverage
  - skips a test when Datadog has a record of it passing on a commit where its covered and tracked files are identical
  - requires `dd-trace` ≥ 2.22.0 for .NET
  - supports an "unskippable" attribute (`datadog_itr_unskippable`)
  - backfills coverage for skipped tests
  - may miss tests that depend on non-code inputs (text files, external APIs)

  — [Datadog TIA .NET setup](https://docs.datadoghq.com/tests/test_impact_analysis/setup/dotnet/); [Datadog TIA how it works](https://docs.datadoghq.com/tests/test_impact_analysis/how_it_works/)
- **MSBuildCache can cache test runs** via `/t:Build;Test` with a test target such as `Microsoft.Build.RunVSTest`. — [MSBuildCache README](https://github.com/microsoft/MSBuildCache)
- **Caching test results under Nx** requires configuring a logger and `results-directory`, because `dotnet test` writes no results by default. — [Nx intro](https://canary.nx.dev/docs/technologies/dotnet/introduction)

### Inferences
- **Recommended layering:**
  - (1) graph-affected test projects: sound, cheap
  - (2) cache test-project results keyed on the hash of the test assembly closure plus test data files
  - (3) optional coverage-based per-test selection (Datadog TIA, Azure DevOps TIA)
  - (4) sharding

  Sharding options in .NET:
  - by test project, the natural unit
  - within a project via `--filter`. VSTest uses `FullyQualifiedName~`, traits and categories. MTP has framework-specific filters such as `--treenode-filter` or `--filter-*` depending on the framework.
  - by timing data, using results from earlier runs
- **MTP test apps are executables.** They can be run directly, without `dotnet test`. A monorepo tool can then invoke them per project with its own parallelism and sharding, bypassing VSTest's single-host orchestration. In mixed repos, the global.json runner switch is all-or-nothing per solution or working directory, so a migration has to happen solution by solution.
- **Test inputs are a blind spot.** Runtime-read test data (snapshots, JSON fixtures copied via `None CopyToOutputDirectory`) is visible to evaluation-based hashing only if it is declared as an item. Undeclared reads (`File.ReadAllText("../../testdata")`) are invisible to graph tools *and* to coverage-based TIA.

### Gaps
- **Azure DevOps TIA.** No source fetched. From prior knowledge (unverified this session), it works only with the VSTest task, mostly on Windows and .NET Framework, and is effectively legacy. Needs verification before inclusion.
- Did not find current documentation on built-in sharding for MTP or `dotnet test` in .NET 10, or on MTP filter syntax per framework (MSTest, xUnit v3, NUnit, TUnit).
- No published TIA alternatives specific to .NET (e.g. Launchable/CloudBees Smart Tests, Gradle Develocity for .NET) were researched.

---

## 6. Restore: performance, lock files, static graph restore, CPM, CI caching

### Takeaway
Restore is a separate phase that must run before cached or isolated builds; Nx and MSBuildCache both assume it has run.

Lock files (`packages.lock.json` with `RestoreLockedMode`) give reproducible closures and stable CI cache keys. Restore-algorithm changes since 2024 alter restore results and lock files:
- the NuGet 6.12 resolver rewrite (.NET 9)
- `PrunePackageReference`, on by default for net10.0+ in the .NET 10 SDK

The SDK version matters: implicit package references change with the SDK, so locked mode can break unless global.json pins the SDK with `rollForward: disable`.

### Cited Findings
- **Opting in to lock files.** Set `RestorePackagesWithLockFile=true` to generate `packages.lock.json` at the project root. Once the file exists, restore always uses it, even without the property. — [NuGet: PackageReference in project files (updated Aug 2026)](https://learn.microsoft.com/en-us/nuget/consume-packages/package-references-in-project-files)
- **Restore with a lock file.**
  - NuGet quickly checks whether the dependencies declared in the project (or in dependent projects) have changed. If not, it restores exactly the lock-file packages, without re-resolving the graph.
  - `dotnet restore --locked-mode` (or `-p:RestoreLockedMode=true`) fails instead of updating the lock file.
  - Related options: `--force-evaluate` / `RestoreForceEvaluate` (floating versions) and `--lock-file-path` / `NuGetLockFilePath`, which allows `packages.<project_name>.lock.json` when several projects share a directory.

  — [NuGet PackageReference doc](https://learn.microsoft.com/en-us/nuget/consume-packages/package-references-in-project-files)
- **Format versioning.** The lock-file format is versioned and depends on features used, such as CPM transitive pinning or duplicate effective target frameworks. — [NuGet PackageReference doc](https://learn.microsoft.com/en-us/nuget/consume-packages/package-references-in-project-files)
- **Which lock files to commit.** Libraries and common-code projects' lock files "have little say" over what dependent projects resolve, so NuGet recommends committing lock files only for the leaf apps at the start of a dependency chain. Example: ProjectA has PackageX 2.0.0; ProjectB has PackageX 1.0.0; A's lock file records 2.0.0. — [NuGet PackageReference doc](https://learn.microsoft.com/en-us/nuget/consume-packages/package-references-in-project-files)
- **SDK implicit references.** The .NET SDK adds implicit `PackageReference`s, so a different SDK version changes package versions and causes locked-mode failures. The documented mitigation is global.json with `rollForward: disable`. — [NuGet PackageReference doc](https://learn.microsoft.com/en-us/nuget/consume-packages/package-references-in-project-files)
- **New resolver.** NuGet **6.12** (.NET 9 / VS 17.12) rewrote the restore algorithm for performance and scalability and enabled it by default. `RestoreUseLegacyDependencyResolver=true` reverts. Comparing `project.assets.json` between the two shows exactly one diff field when they agree. — [NuGet PackageReference doc](https://learn.microsoft.com/en-us/nuget/consume-packages/package-references-in-project-files)
- **PrunePackageReference.**
  - Introduced in NuGet 6.13 / .NET SDK 9.0.200 (opt-in). **On by default for projects targeting net10.0+ in the .NET 10 SDK**, and it now covers direct packages too.
  - Pruned transitive packages are not downloaded.
  - When a direct package can be removed from all frameworks, NuGet raises NU1510. A pruned package that matches a `ProjectReference` raises NU1511.
  - Lock files regenerated with pruning will shrink; the locked-mode check is pruning-aware.
  - Benefits: performance, and fewer NuGetAudit false positives.

  — [NuGet PackageReference doc](https://learn.microsoft.com/en-us/nuget/consume-packages/package-references-in-project-files)
- **Duplicate effective target frameworks.** Supported since NuGet 7.6 / .NET SDK 10.0.300, for multi-RID or benchmarking scenarios. They affect the lock-file format version, and packing them requires disambiguation (NU5051). — [NuGet PackageReference doc](https://learn.microsoft.com/en-us/nuget/consume-packages/package-references-in-project-files)
- **Floating versions** (`3.6.*`) and newly published packages cause non-reproducible restores without lock files. — [NuGet PackageReference doc](https://learn.microsoft.com/en-us/nuget/consume-packages/package-references-in-project-files)
- **Static graph restore.** `RestoreUseStaticGraphEvaluation=true` (`dotnet restore -p:RestoreUseStaticGraphEvaluation=true`) evaluates projects via the static graph instead of running the `_GenerateRestoreGraph` targets per project. It is recommended for large repos for speed. (canonical doc, not re-fetched) — [NuGet: restore with MSBuild static graph evaluation](https://learn.microsoft.com/en-us/nuget/reference/msbuild-targets#restoring-with-msbuild-static-graph-evaluation)
- **CPM transitive pinning.** `CentralPackageTransitivePinningEnabled=true` makes `PackageVersion` entries also pin transitive packages. (canonical doc, not re-fetched) — [NuGet: Central Package Management](https://learn.microsoft.com/en-us/nuget/consume-packages/central-package-management#transitive-pinning)
- **Restore and the static graph protocol.** The static-graph spec says restore "doesn't map cleanly" onto the protocol. — [static-graph spec](https://github.com/dotnet/msbuild/blob/main/documentation/specs/static-graph.md)
- **Restore under Nx and MSBuildCache.** Nx doesn't infer restore by default; you run `dotnet restore` before build, and build uses `--no-restore`. Nx hashes `NUGET_PACKAGES`, all `nuget.config` files, and `Directory.Packages.props`. — [Nx intro](https://canary.nx.dev/docs/technologies/dotnet/introduction)
- **NuGet CI caching on GitHub Actions.** `actions/setup-dotnet` has `cache: true`, keyed on `packages.lock.json` (configurable via `cache-dependency-path`). It caches the global packages folder (`NUGET_PACKAGES`). (canonical doc, not re-fetched) — [actions/setup-dotnet README: caching NuGet packages](https://github.com/actions/setup-dotnet#caching-nuget-packages)

### Inferences
- **A CI recipe:**
  1. Pin the SDK with global.json.
  2. Use CPM with `Directory.Packages.props`, optionally with transitive pinning.
  3. Use lock files on leaf apps (or all projects) and restore in locked mode in CI.
  4. Cache `~/.nuget/packages` (or a repo-local `NUGET_PACKAGES`) keyed on hash(all `packages.lock.json` + `Directory.Packages.props` + `nuget.config` + global.json).
  5. Run one solution- or traversal-level restore with `RestoreUseStaticGraphEvaluation=true`.
  6. Build with `--no-restore`.
- **The obj/ ownership problem.** `project.assets.json` and `nuget.g.props`/`.targets` contain absolute paths to the package root. Caching `obj/` across machines therefore requires the same `NUGET_PACKAGES` path. Restore is better treated as an always-run, cheap-if-cached step, and restore outputs kept out of build caches (as Nx does).
- **Affected detection depends on the SDK band.** PrunePackageReference and the new resolver mean an SDK band upgrade can change many lock files and `project.assets.json` files without any project edits. An affected tool should treat global.json or SDK changes as global invalidation.

### Gaps
- No quantitative restore benchmarks were fetched: static graph restore vs standard, or the NuGet 6.12 resolver improvement figures.
- No guidance was fetched for Azure Pipelines `Cache@2` with NuGet, or for repo-local package folders.
- Did not verify whether `RestoreUseStaticGraphEvaluation` became the default in .NET 10.
