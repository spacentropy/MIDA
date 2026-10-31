# Fragmented tools leave .NET monorepos half-served

As of October 2026, no tool covers the full .NET monorepo stack. That stack is MSBuild-faithful project discovery, affected detection that understands MSBuild semantics, a remote cache that works across machines, distributed execution, release orchestration for many NuGet packages, and a workflow that keeps the IDE working. The pieces exist, but they are spread across about a dozen projects of uneven maturity. Nx is the only mainstream polyglot orchestrator with first-party .NET support: **@nx/dotnet graduated from experimental in Nx 23 (June 2026)**. But an open issue shows its remote cache **produces broken builds across machines, because `obj/` embeds absolute paths**. Bazel's rules_dotnet is hermetic, but it bypasses MSBuild and the IDE, and it has only 211 stars. Microsoft's MSBuildCache is the most MSBuild-faithful cache, but it is a **0.1.x preview** with Azure-centric backends, and in practice it runs only on Windows. Project-level affected detection is a solved commodity: **dotnet-affected has about 4.55M downloads**. Nothing above that layer is: no free tool caches outputs, and per-test impact analysis is mostly paid. Every large public .NET repo examined hand-rolls the same glue in PowerShell, MSBuild targets and CI YAML. That includes OpenTelemetry, the Azure SDK, Aspire and Polly. The glue maps tags to projects, defines release units, manages API baselines, detects changes and aggregates required checks. For a new tool, the opening is to be the MSBuild-native layer that combines these. It would ship as a dotnet tool with no Node dependency, and build on primitives Microsoft is now shipping rather than replace MSBuild. Those primitives are ProjectGraph, the project-cache plugin API, slnx, and a persistent evaluation cache that is still in progress. The demand is real but scattered: GitHub issues get few votes, many tools are tiny 0.x projects, and paid test-impact products exist. One caveat: Reddit sentiment could not be collected for this report.

## Nx leads orchestration, but no tool covers the whole stack

### Nx @nx/dotnet is the incumbent, but its cache breaks across machines

Nx (29,435 stars, release 24.0.2 on 2026-10-26) is the tool a new entrant will be compared against first ([GitHub nrwl/nx](https://github.com/nrwl/nx)). Its official .NET plugin started as **22.0.0-beta.8 on 2025-10-19** ([newreleases](https://newreleases.io/project/npm/nx/release/22.0.0-beta.8)) and **graduated out of experimental in Nx 23**, per a release blog dated 2026-06-17 ([Nx 23 release blog](https://nx.dev/blog/nx-23-release)). Version labels conflict across sources: jsDelivr lists 23.2.1 while the npm readme still showed the experimental banner, so check npm directly ([jsDelivr](https://www.jsdelivr.com/package/npm/@nx/dotnet)).

How it works:

- **Discovery.** It scans `**/*.csproj`, `**/*.fsproj` and `**/*.vbproj` and infers targets with an MSBuild-based analyzer. It requires **.NET SDK 8+**.
- **Graph.** `<ProjectReference>` items become graph edges, which is what powers `nx affected` and `nx graph` ([Nx 23 release blog](https://nx.dev/blog/nx-23-release)).
- **Targets.** It infers `build`, `test`, `restore`, `clean`, `publish` (Exe projects), `pack` (libraries) and `watch`.
- **Execution.** Build runs as `dotnet build --no-restore --no-dependencies` with `dependsOn: ["^build"]`. So Nx, not MSBuild, orders the projects, and each MSBuild invocation covers a single project ([Nx migration KB](https://nx.dev/docs/kb/migrate-from-nx-dotnet-core)).
- **Cache inputs.** The hash covers:
  - the project's own files
  - outputs of the projects it depends on
  - the nearest `Directory.Build.props`/`.targets`/`.rsp`, `Directory.Solution.*`, `Directory.Packages.props` and `global.json`
  - every `nuget.config` and `.editorconfig` between the project and the workspace root
  - items linked from other directories
  - the `NUGET_PACKAGES` environment variable ([Nx .NET intro](https://canary.nx.dev/docs/technologies/dotnet/introduction))

The community plugin @nx-dotnet/core is archived; its last release was v3.0.2 in August 2025 ([GitHub nx-dotnet](https://github.com/nx-dotnet/nx-dotnet)). The official plugin dropped its generators, its `format` target, its `moduleBoundaries` support and its NuGet version-sync generator. The replacements are `dotnet new`, Nx Conformance and Central Package Management ([Nx migration KB](https://nx.dev/docs/kb/migrate-from-nx-dotnet-core)).

The weakness that matters most for a competitor is **issue #35934, opened 2026-06-10 and still open**. `obj/project.assets.json` and `*.nuget.g.props` embed absolute NuGet-cache and project paths. A cache hit restored on a CI agent with a different working directory failed with `CS0006: Metadata file ... could not be found`. The reporter says this makes Nx remote caching, and by extension Nx Agents, "unusable for C# projects unless every machine happens to share identical absolute paths" ([nrwl/nx#35934](https://github.com/nrwl/nx/issues/35934)). Other open issues report:

- `--runtime` breaking build and publish (#33474)
- `restore` mutating `packages.lock.json` (#33475)
- analyzer-target failures on missing folders (#35719) ([GitHub nrwl/nx issues](https://github.com/nrwl/nx/issues?q=dotnet))

Nx 22.7 added task sandboxing that catches hidden cache dependencies through real I/O tracing, along with worktree-aware caching. Patch 22.7.7 then had to declare `Directory.Build.props` as an input for analyzer tasks ([Nx 22.7 blog](https://nx.dev/blog/nx-22-7-release); [newreleases 22.7.7](https://newreleases.io/project/npm/nx/release/22.7.7)). That is live evidence that MSBuild's implicit inputs are hard to model from outside.

Two structural costs follow from Nx's design:

- **It needs a Node toolchain.** Practitioners worry about tools that "wrap around the dotnet CLI" and might break Rider debugging ([phan.nz](https://www.phan.nz/posts/doing-monorepo-dotnet/)).
- **It spawns one `dotnet` process per project.** Each process pays MSBuild startup and evaluation costs that a single `msbuild -graph -m` invocation would amortize.

No published benchmarks of Nx on .NET and no named large adopters were found.

### Hermetic build systems sacrifice MSBuild and the IDE

**rules_dotnet** (bazel-contrib) is the only maintained hermetic option.

- **Status.** v0.22.2 (2026-08-28), minimum Bazel 9.0.0, 211 stars ([GitHub rules_dotnet](https://github.com/bazel-contrib/rules_dotnet)).
- **Positioning.** It presents itself as "a alternative to using MSBuild with .Net". "MSBuild is not used behind the scenes"; it calls the compilers directly ([rules_dotnet docs](https://github.com/bazel-contrib/rules_dotnet/blob/master/docs/README.md)).
- **Workloads.** Razor, Blazor WebAssembly (net10.0+), NativeAOT (not on Windows), and container images via rules_img. VB.NET and Blazor WASM AOT are unsupported.
- **NuGet.** NuGet flows through Paket and paket2bazel. Issues for a native NuGet resolver (#530) and Central Package Management (CPM) lock-file support (#444) are open, as is the `ImplicitUsings` gap (#436) ([rules_dotnet issues](https://github.com/bazel-contrib/rules_dotnet/issues)).
- **IDE.** The docs concede that "for proper IDE support the MSBuild project files need to be manually maintained" ([rules_dotnet docs mirror](https://github.com/bazelbuild/rules_dotnet/blob/master/docs)).
- **BUILD files.** Users have asked for Gazelle-style BUILD-file generation (#258) because hand-maintaining build metadata is a blocker ([rules_dotnet #258](https://github.com/bazelbuild/rules_dotnet/issues/258)).

Reimplementing MSBuild means re-porting every SDK feature, which explains the long tail of issues. No production users were named.

The other hermetic systems are effectively non-options:

- **Buck2.** Its prelude `csharp_library` invokes `csc` directly, and its toolchain knows only `csc` and `framework_dirs`. There is no NuGet or SDK-style project support ([buck2 prelude/csharp](https://github.com/facebook/buck2/tree/main/prelude/csharp)).
- **Pants.** It has no C# backend. Its feature request #21581 has been open since 2024 and is labelled stale ([pants#21581](https://github.com/pantsbuild/pants/issues/21581)).
- **Please.** No C# rules exist ([GitHub please](https://github.com/thought-machine/please)).
- **Remote-execution vendors.** EngFlow and BuildBuddy do not list MSBuild among supported clients ([EngFlow docs](https://docs.engflow.com/overview.html); [BuildBuddy RBE](https://docs.buildbuddy.io/docs/remote-build-execution)).

That leaves **remote execution for MSBuild with no commercial champion**.

### JS orchestrators and .NET script frameworks model the wrong graph

Turborepo (31,267 stars) calls itself a "Build system optimized for JavaScript and TypeScript". Its only .NET-related issue concerned `dotnet restore`/`publish` friction ([turborepo#11114](https://github.com/vercel/turborepo/issues/11114)). Rush, Lage and Bit have no .NET integration ([GitHub rushstack](https://github.com/microsoft/rushstack); [GitHub lage](https://github.com/microsoft/lage); [GitHub bit](https://github.com/teambit/bit)). These tools hash files plus a package.json graph and know nothing about MSBuild imports, so they would produce false cache hits.

**moon** is the closest JS-world tool. Its v2 WASM toolchain plugins are stable, and maintainer milesj invited a .NET plugin in issue #2447. A community plugin offers MSBuild-evaluated graphs and lockfile-aware hashing, but it has **3 stars**. Its PR to the official plugin repo (#169) has been open since 2026-07-26. Its author notes that moon "can't discover .NET projects on its own" from `*.csproj` globs ([moon#2447](https://github.com/moonrepo/moon/issues/2447); [moonrepo/plugins#169](https://github.com/moonrepo/plugins/pull/169); [moon-dotnet-plugin](https://github.com/Wtiben/moon-dotnet-plugin)). The requirement this exposes is that a .NET tool must discover projects from `*.csproj`, `*.slnx` and `dirs.proj` with **zero per-project manifests**.

The .NET-native automation frameworks build a DAG of *pipeline steps*, not of projects:

| Framework | Stars | Latest release | Downloads |
|---|---|---|---|
| Cake | 4,198 | v6.3.0 (2026-09-14) | ~176.4M (Cake.Tool) |
| NUKE | — | 10.1.0 (2025-12-02), no pushes since | ~41.3M |
| FAKE | — | 6.2.0 | — |
| Bullseye | — | — | ~5.9M |
| ModularPipelines | 559 | — | ~479K |

Cake dominates ([GitHub cake](https://github.com/cake-build/cake); [NuGet Cake.Tool](https://www.nuget.org/packages/Cake.Tool)). An April 2026 blog says NUKE's maintainer "has reached a breaking point" and calls the project "effectively abandoned". **No official announcement confirms this** ([GitHub nuke](https://github.com/nuke-build/nuke); [Bruno Massa blog](https://brunomassa-com-blog-hugo.pages.dev/en/blog/the-nuke-uncertainty-when-a-maestro-steps-away/)). ModularPipelines is the active newcomer, with `[DependsOn<T>]` modules and DI ([GitHub ModularPipelines](https://github.com/thomhurst/ModularPipelines)). Teams pair these frameworks with dotnet-affected or Incrementalist to get monorepo behavior.

### Comparison: no row has every column

| Tool | Graph discovery | Affected | Remote cache | Distributed exec | NuGet/CPM awareness | IDE story | Status |
|---|---|---|---|---|---|---|---|
| Nx + @nx/dotnet | Inferred from csproj via MSBuild analyzer | Yes | Nx Cloud, broken cross-machine (#35934) | Nx Agents (same caveat) | MSBuild-derived inputs | Plain csproj | GA in Nx 23; 24.0.2 |
| Bazel + rules_dotnet | Hand-written BUILD files | Via query | Yes (REAPI) | Yes (RBE) | Paket only; no CPM lock (#444) | Manual csproj upkeep | v0.22.2 |
| MSBuild + MSBuildCache | `-graph` | No | Azure Blob / Pipelines / 1ES | No | SDK version in fingerprint | Native | 0.1.340-preview |
| MSBuild + Traversal | dirs.proj globs | No | No | No | CPM native | Native | Traversal 4.1.82 |
| BuildXL | MSBuild static graph | None found | Yes | Yes (Microsoft-scale) | None found | No VS integration | No GitHub releases |
| dotnet-affected | MSBuild ProjectGraph + git diff | Yes, CPM-aware | No | No | Per-package CPM diff | Emits .slnf / traversal | v7.0.1 (2026-10-07) |
| Incrementalist | libgit2sharp + Roslyn | Yes (coarse) | No | No | Not mentioned | None | 1.2.2 (2026-08-18) |
| moon + community plugin | MSBuild graph, no glob discovery | Yes | moonbase | None found | Lockfile hashing (claimed) | Plain csproj | Plugin unmerged |
| Turborepo / Rush / Lage | package.json only | JS only | Opaque scripts | None found | None | None | No .NET integration |
| Cake / NUKE / FAKE / ModularPipelines | Step DAG | No | No | No | None found | C# scripts | Cake active; NUKE uncertain |

Sources: the per-tool citations above, plus [dotnet-affected](https://github.com/leonardochaia/dotnet-affected), [Incrementalist](https://github.com/petabridge/Incrementalist), [MSBuildCache](https://github.com/microsoft/MSBuildCache) and [BuildXL](https://github.com/microsoft/BuildXL).

## Plain MSBuild is a credible baseline with preview-grade caching

Before building anything, a tool designer should know how far stock MSBuild already goes. The **Microsoft.Build.Traversal** SDK (version 4.1.82, about 12.7M downloads) and **NoTargets** (about 12.5M downloads) are heavily adopted ([NuGet Traversal](https://www.nuget.org/packages/Microsoft.Build.Traversal); [NuGet NoTargets](https://www.nuget.org/packages/Microsoft.Build.NoTargets)). A `dirs.proj` can glob `src\**\*.*proj` and nest per folder, which "often replac[es] Visual Studio solution files for large trees" ([Traversal README](https://github.com/microsoft/MSBuildSdks/blob/main/src/Traversal/README.md)).

`msbuild -graph` builds a static graph and runs it in topological order. `-graph -isolate` turns any `MSBuild` task call the graph did not predict into an error. The `/inputResultsCaches` and `/outputResultsCache` switches serialize build results so "a higher-order build engine" can cache and distribute single-project builds. The spec states plainly that this is **"currently only implemented internally at Microsoft"** ([MSBuild static-graph spec](https://github.com/dotnet/msbuild/blob/main/documentation/specs/static-graph.md)).

The same spec lists the static graph's limits:

- It is a conservative superset derived from evaluation-time items.
- Multi-targeting creates *speculative* edges to all inner builds.
- Cycles are banned.
- I/O tracking is on hold.
- Restore "doesn't map cleanly" onto the graph.

### The project-cache plugin API exists, but its only implementation is Windows-bound

MSBuild has shipped a pluggable project cache since **MSBuild 16.9 (cache get) and 17.8 (cache add)**. A plugin implements:

- `BeginBuildAsync`
- `GetCacheResultAsync`
- `HandleFileAccess`/`HandleProcess`
- `HandleProjectFinishedAsync`
- `EndBuildAsync`

On a cache hit, the plugin chooses one of three ways to satisfy the request: fabricated target results (brittle), cheap proxy targets, or a serialized `BuildResult`. The last is the most correct, but it carries serialization-compatibility risk. Cache add depends on Detours-based file-access reporting, which costs **about 10–15%** and is limited to **MSBuild.exe, x64, command-line only**. Visual Studio cannot add to the cache ([MSBuild project-cache spec](https://github.com/dotnet/msbuild/blob/main/documentation/specs/project-cache.md)).

Microsoft's **MSBuildCache** implements this API.

- **Status.** 66 stars; v0.1.340-preview on 2026-08-28; requires VS 17.9+ and `msbuild /graph /m /reportfileaccesses`.
- **Backends.** A BuildXL local cache (100 GB default), Azure Blob, Azure Pipelines caching and 1ES Build Cache.
- **C# requirement.** C# builds need an extra SharedCompilation package, because the detached Roslyn compiler server's file accesses are otherwise invisible ([MSBuildCache README](https://github.com/microsoft/MSBuildCache)).
- **Adoption.** Downloads are low: about 32K (Local), 51K (Azure Blob) and 21K (Azure Pipelines) ([NuGet](https://www.nuget.org/packages/Microsoft.MSBuildCache.Local)).
- **Security.** The README says the Blob backend "does not yet have a robust security model": an external contributor's PR could overwrite cache content.

Its configuration surface is the best public blueprint for a correct MSBuild cache:

- `MSBuildCacheIgnoredOutputPatterns` defaults to `*.assets.cache; *assemblyreference.cache` because those files are "not properly portable".
- The exact SDK patch version is in the fingerprint unless you opt out.
- Probe and enumeration fingerprinting catches glob changes.
- `MSBuildCacheTouchOutputFiles`/`SkipUnchangedOutputFiles` manage timestamps.

Users find the model confusing. One expected source-hash skipping and learned that the cache **replays per-project file writes** instead ([MSBuildCache #104](https://github.com/microsoft/MSBuildCache/issues/104)). Whether MSBuildCache now works with `dotnet build` on Linux or macOS could not be confirmed. The spec's MSBuild.exe/x64 restriction implies it does not.

**BuildXL** is open source and active (1,024 stars). At Microsoft it runs "150,000+ builds per day on monorepo codebases up to a half-terabyte in size with a half-million process executions per build". It has an MSBuild frontend and runs on Ubuntu, but there are "no plans to integrate it into Visual Studio" and no GitHub releases ([BuildXL README](https://github.com/microsoft/BuildXL)). No public distribution of QuickBuild or CloudBuild was found.

### Microsoft is building the primitives a third-party tool needs

MSBuild epic **#14234, opened 2026-07-01, proposes a persistent evaluation cache**. Today, caches last only one build, and Microsoft notes "_billions_ of invocations each month". The design includes sharing cache files across hosts through remote stores or CI artifacts. Sub-issues cover an evaluation input-observation layer (#14688) and invalidating build servers (dotnet/sdk#56045) ([dotnet/msbuild #14234](https://github.com/dotnet/msbuild/issues/14234)). Earlier requests show which hooks are still missing:

- exposing MSBuild's up-to-date hash so third parties can build distributed caching (#9122, closed)
- making evaluation results machine-agnostic (#6182, backlog) ([msbuild #9122](https://github.com/dotnet/msbuild/issues/9122); [msbuild #6182](https://github.com/dotnet/msbuild/issues/6182))

A MSBuild-native stack is a fair "no new tool" baseline: dirs.proj, CPM, `-graph -m`, MSBuildCache, dotnet-affected and SlnGen. Its weaknesses are fragmentation, preview-quality caching, and no model for tasks outside MSBuild such as lint, Docker and JS.

## Affected detection works at project level; test level remains paywalled

Every mature tool follows the same three steps:

1. Git-diff against a base ref.
2. Map each changed file to the projects that own it.
3. Take the reverse-transitive closure over the `ProjectReference` graph.

The tools differ almost entirely on step 2, at one of three levels of fidelity:

- **Path containment** is what naive scripts use. It is wrong for linked files and glob exclusions.
- **Static XML parsing** is the middle level.
- **Full MSBuild evaluation** enumerates `Compile`, `EmbeddedResource`, `Content` and `AdditionalFiles` items, plus `Project.Imports`.

Only evaluation catches `<Compile Include="..\..\Shared\Foo.cs">` and `<Compile Remove="Legacy/**"/>`.

**dotnet-affected** sits at the evaluation level and leads the niche (413 stars, v7.0.1 on 2026-10-07, about 4.55M downloads).

- **Inputs.** It walks the MSBuild graph from changed files. It detects changes in "any file they reference", including `Directory.Build.props`/`.targets`.
- **CPM.** It diffs `Directory.Packages.props` so that only projects referencing a changed package are marked.
- **Outputs.** It emits a Traversal `affected.proj`, a `.slnf`, text or JSON. It **exits 166** when nothing changed ([dotnet-affected README](https://github.com/leonardochaia/dotnet-affected)).
- **Speed.** v7 is "6.5× faster than v6.2.0" on a 2,000-project repo ([dotnet-affected.com](https://dotnet-affected.com/)). That suggests evaluation and graph construction are the bottleneck at scale.

Petabridge's **Incrementalist** (about 576K downloads, 1.2.2 in August 2026) uses libgit2sharp and Roslyn. It falls back to "build everything" when `Directory.Build.props`, `global.json` or `.sln` changes, and its algorithm does not mention `Directory.Packages.props` ([Incrementalist how-it-works](https://raw.githubusercontent.com/petabridge/Incrementalist/dev/docs/how-it-works.md)). Smaller entrants such as `dotnet-monorepo` (0.0.x), DotMR.Cli (0.0.13) and JustBuild show that many people are trying ([NuGet dotnet-monorepo](https://packages.nuget.org/packages/dotnet-monorepo); [NuGet DotMR.Cli](https://www.nuget.org/packages/dotmr.cli)).

### Aspire's SelectTests is the blueprint to productize

The most advanced selector found is not a product. It lives inside the **microsoft/aspire** CI.

- **Two layers.** "Layer 1" is an MSBuild `ProjectGraph` over `Aspire.slnx`. "Layer 2" is a curated trigger map for things outside MSBuild: the TypeScript SDK, the VS Code extension and installers.
- **Enforcement.** In enforce mode it writes an `OverrideProjectToBuild` props file that restricts the build. In audit mode it runs everything and reports what it would have skipped.
- **Diff base.** It diffs against the PR's real head rather than the synthetic merge ref, so the selection does not grow when the base branch advances.
- **Escape hatch and explainability.** A `run-full-ci` label acts as a kill switch. Each selection posts a summary comment and a JSON record of its cause ([aspire tests.yml](https://github.com/microsoft/aspire/blob/main/.github/workflows/tests.yml); [select-tests action](https://github.com/microsoft/aspire/blob/main/.github/actions/select-tests/action.yml)).

Audit mode, explainability, an escape hatch and inputs outside MSBuild are exactly what dotnet-affected lacks.

### Blind spots that remain open

Several inputs defeat today's tools:

- **CPM transitive pinning.** With `CentralPackageTransitivePinningEnabled`, a changed pin affects projects that consume the package only *transitively*. Detecting that requires reading `project.assets.json` or lock files, and dotnet-affected's docs do not say whether it does.
- **Deleted linked files.** These require evaluating the base commit too. Neither tool documents how it handles them.
- **SDK changes.** A `global.json` or SDK-band change alters implicit package references, analyzers and the compiler. It should invalidate everything. Nx hashes `global.json`, and Incrementalist rebuilds everything.
- **Undeclared reads.** Generators or tests that read files directly (`File.ReadAllText("../../testdata")`) are invisible to every evaluation-based tool. They are also invisible to coverage-based test impact analysis, which "can miss tests that depend on non-code inputs" ([Datadog TIA](https://docs.datadoghq.com/tests/test_impact_analysis/how_it_works/)).
- **Multi-targeting.** Speculative edges mean one TFM-specific change can mark every TFM affected ([static-graph spec](https://github.com/dotnet/msbuild/blob/main/documentation/specs/static-graph.md)).

For queries, `dotnet msbuild -getProperty/-getItem/-getTargetResult` (MSBuild 17.8+) returns JSON. That JSON includes `DefiningProjectFullPath`, which shows which `.props` file added an item ([MS Learn: evaluate items and properties](https://learn.microsoft.com/en-us/visualstudio/msbuild/evaluate-items-and-properties)). Even so, a long-lived host process that shares an `EvaluationContext` will beat spawning a process per project once a repo passes 1,000 projects.

### Test selection is shifting under .NET 10's testing platform

The .NET 10 SDK added a native Microsoft.Testing.Platform (MTP) mode for `dotnet test`, selected through `global.json` (`"test": {"runner": "Microsoft.Testing.Platform"}`) and requiring MTP 1.7+. In MTP mode, **VSTest projects in the same solution cannot run** ([MS Learn: unit testing with dotnet test](https://learn.microsoft.com/dotnet/core/testing/unit-testing-with-dotnet-test); [.NET blog](https://devblogs.microsoft.com/dotnet/dotnet-test-with-mtp/)). A third-party October 2026 post reports that `dotnet test` **silently falls back to VSTest** when CI runs from a directory where `global.json` isn't found ([startdebugging.net](https://startdebugging.net/de/2026/10/fix-dotnet-test-falls-back-to-vstest-in-linux-ci-microsoft-testing-platform/)). Because MTP test apps are executables, a monorepo tool can run them directly with its own parallelism and sharding.

Below project granularity, the market is mostly paid:

- **Datadog Test Impact Analysis** skips tests whose covered files are unchanged and requires dd-trace ≥ 2.22.0 ([Datadog TIA .NET](https://docs.datadoghq.com/tests/test_impact_analysis/setup/dotnet/)).
- **dotnet-tia** (v0.8.0, about 1.3K downloads) is the open-source challenger. It says per-test analysis has existed "only behind a paywall or SaaS (Datadog Test Optimization, Tricentis SeaLights, NCrunch – IDE-only)". It reports a mean selection of 35.5% of tests on NodaTime and 51.0% on FluentValidation over replayed commits; these are its own figures ([NuGet dotnet-tia](https://www.nuget.org/packages/dotnet-tia)).

Paid products here are evidence that teams will pay for this category.

## Absolute paths, timestamps and restore make .NET outputs hard to cache

MSBuild incrementality is per-target timestamp comparison, with no content hashing ([MS Learn: incremental builds](https://learn.microsoft.com/en-us/visualstudio/msbuild/incremental-builds)). Reference assemblies (on by default for .NET 5+) help: `CopyRefAssembly` leaves `obj/ref/` untouched when only private implementation changed, so downstream `CoreCompile` is skipped ([MS Learn: reference assemblies](https://learn.microsoft.com/en-us/dotnet/standard/assembly/reference-assemblies)). A no-op `dotnet build` in a big repo still pays for:

- implicit restore
- evaluation of every project and its many SDK imports
- `ResolveProjectReferences` re-entry
- timestamp checks on copies

Visual Studio's fast up-to-date check avoids some of this, but the CLI doesn't use it ([project-system docs](https://github.com/dotnet/project-system/blob/main/docs/up-to-date-check.md)).

### Why .NET outputs resist caching

The sources point to five reasons:

1. **Absolute paths are embedded.** They appear in `project.assets.json` and `nuget.g.props`, in serialized `BuildResult`s, in `*.AssemblyReference.cache`, and in PDBs unless `ContinuousIntegrationBuild`/PathMap is set ([project-cache spec](https://github.com/dotnet/msbuild/blob/main/documentation/specs/project-cache.md); [MS Learn: ContinuousIntegrationBuild](https://learn.microsoft.com/en-us/dotnet/core/project-sdk/msbuild-props#continuousintegrationbuild)).
2. **`obj/` mixes restore state with incremental state.**
3. **Restored outputs get fresh timestamps**, which fools the next incremental build.
4. **Out-of-process tools escape file tracing**, notably the compiler server.
5. **Commit-dependent stamping changes outputs on every commit.** SourceLink, GenerateAssemblyInfo, MinVer and Nerdbank.GitVersioning inject commit data even when code is unchanged.

Nx's #35934 is reason 1 in production. Nx's workaround already excludes restore's top-level `obj` files from outputs, so a cache hit does not replace restore ([Nx .NET intro](https://canary.nx.dev/docs/technologies/dotnet/introduction)).

A cross-platform alternative to Detours is a Gradle-style per-project key. It would hash:

- the evaluated items
- the content of imported props and targets
- the resolved package set (ID, version and `.nupkg.sha512` from `project.assets.json`)
- the **ref assemblies** of dependencies, not their implementation DLLs
- the SDK version
- relevant global properties
- `.editorconfig`/`.globalconfig` files

Hashing ref assemblies reproduces the "implementation-only change doesn't recompile dependents" benefit. Version stamping should stay out of the key, or run only at pack and publish. This trades some soundness, since tasks can read undeclared files, for portability. The relocation problem then becomes a design choice among fixed checkout paths, path rewriting on restore, or caching only `bin/` and ref assemblies and always re-running restore.

### Restore is its own phase, and SDK upgrades rewrite it

Lock files (`RestorePackagesWithLockFile`, `--locked-mode`) give reproducible package closures. NuGet recommends committing them only for **leaf apps**, because library lock files "have little say" over what dependents resolve. Since the SDK adds implicit package references, locked mode breaks on SDK drift unless `global.json` pins the SDK with `rollForward: disable` ([NuGet PackageReference docs](https://learn.microsoft.com/en-us/nuget/consume-packages/package-references-in-project-files)).

Three recent changes alter restore output without any project edit:

- **NuGet 6.12 (.NET 9)** rewrote the resolver.
- **PrunePackageReference** became the default for net10.0+ in the .NET 10 SDK. It drops pruned packages and shrinks lock files.
- **NuGet 7.6 (SDK 10.0.300)** added duplicate effective target frameworks, which bumps the lock-file format version (same source).

An affected tool should therefore treat an SDK-band change as global invalidation. Static-graph restore (`RestoreUseStaticGraphEvaluation=true`) is recommended for large repos ([NuGet MSBuild targets](https://learn.microsoft.com/en-us/nuget/reference/msbuild-targets#restoring-with-msbuild-static-graph-evaluation)). `actions/setup-dotnet` can cache the global packages folder keyed on lock files ([setup-dotnet README](https://github.com/actions/setup-dotnet#caching-nuget-packages)).

No public repo examined does MSBuild *output* caching in CI. They rely on NuGet package caching plus selective builds. Polly, for example, pins `NUGET_PACKAGES` to a fixed path ([Polly build.yml](https://github.com/App-vNext/Polly/blob/main/.github/workflows/build.yml)).

## Conventions have converged on CPM, slnx and an eng folder

### Directory.Build.props stops at the first match, so nesting silently breaks

MSBuild imports only the **first** `Directory.Build.props` (and separately `.targets`) found walking up from the *project*. "The location of the solution file is irrelevant." Nested files must chain to their parent explicitly with `GetPathOfFileAbove`, and "the first Directory.Build.props that doesn't import anything is where MSBuild stops". Other pitfalls ([MS Learn: customize by directory](https://learn.microsoft.com/en-us/visualstudio/msbuild/customize-by-directory)):

- Properties the SDK reads early, such as `BaseIntermediateOutputPath`, have no effect outside `.props`.
- The file name is case-sensitive on Linux.
- `Directory.Build.rsp` applies only to CLI builds, not Visual Studio.
- `.targets` is imported after NuGet package targets, so it can override them.

A linter that flags nested files that don't chain, wrong-case names, and early-read properties set in the wrong file would catch the most common silent misconfigurations.

**Central Package Management** has been the default way to govern versions since it shipped on 2022-04-12 ([NuGet blog](https://devblogs.microsoft.com/nuget/introducing-central-package-management/)). It has the same nearest-file-wins rule: "Only one Directory.Packages.props file is evaluated for a given project by default", so parents must be imported explicitly ([MS Learn: CPM](https://learn.microsoft.com/en-us/nuget/consume-packages/central-package-management)). Its sharp edges:

- **Downgrades fail.** Transitive pinning cannot pin below a dependency's floor; that is error NU1109.
- **Libraries leak pins.** Pinned transitive packages become explicit nuspec dependencies when a library is packed.
- **Feeds need mapping.** Multiple feeds without package source mapping trigger NU1507.
- **Overrides erode central control.** `VersionOverride` weakens centralization unless `CentralPackageVersionOverrideEnabled=false` is set.
- **Analyzers go global.** `GlobalPackageReference` (NuGet 6.4+) installs analyzers or versioning packages repo-wide.

The cost is visible in dotnet/fsharp PR #20084 (August 2026). Turning on transitive pinning there meant raising central pins "to those floors so transitive pinning does not downgrade them" ([dotnet/fsharp #20084](https://github.com/dotnet/fsharp/pull/20084)). The Azure SDK runs a `Validate-CpmCompliance.ps1` script ([Azure SDK eng/scripts](https://github.com/Azure/azure-sdk-for-net/tree/main/eng/scripts)). Enforcement is bespoke today, and no policy or audit tool exists.

### Solutions are becoming derived views

Since the **.NET 10 SDK, `dotnet new sln` creates `.slnx` by default** ([MS Learn breaking change](https://learn.microsoft.com/en-us/dotnet/core/compatibility/sdk/10.0/dotnet-new-sln-slnx-default)).

- **Migration.** `dotnet sln migrate` (SDK 9.0.200+) converts existing files. Developers report that the GUID-heavy `.sln` format caused frequent merge conflicts, worst in "large monorepo solutions with hundreds of projects" ([Milan Jovanovic](https://milanjovanovic.tech/blog/the-new-slnx-solution-format-migration-guide)).
- **Filters.** Solution filters (`.slnf`) still work on top of `.slnx` ([VS blog](https://devblogs.microsoft.com/visualstudio/?p=252703)).
- **IDE support.** Rider has read `.slnx` since 2024.3 ([JetBrains blog](https://blog.jetbrains.com/dotnet/2024/10/04/support-for-slnx-solution-files/)).
- **Generation.** **SlnGen** (v12.0.58 on 2026-10-23, about 1.63M downloads) generates solutions from the project graph. Its 12.0.47 release added a new parser "to later enable .slnx support". Whether `.slnx` output is the default could not be confirmed ([slngen releases](https://github.com/microsoft/slngen/releases); [NuGet SlnGen](https://www.nuget.org/packages/Microsoft.VisualStudio.SlnGen.Tool)).

`.slnx` fixes merge conflicts at the format level. That shifts the remaining problem from conflict resolution to *generating and syncing* subsets: per-team or per-task views, such as "project X plus its closure", produced from the graph. Filters must be regenerated together with their parent solution, because a `.slnf` is bound to one solution path.

### SDK pins, artifacts output and Aspire set the root conventions

The Arcade SDK documents the canonical layout used across dotnet/* repos ([ArcadeSdk.md](https://github.com/dotnet/arcade/blob/main/Documentation/ArcadeSdk.md)):

- `src/` with any substructure
- test projects suffixed `.UnitTests`/`.IntegrationTests`
- `eng/` for engineering infrastructure (`common/`, `Versions.props`, `Build.props`)
- a single gitignored `artifacts/` output root
- at the root: `global.json`, `NuGet.config`, `Directory.Build.*`, and thin `build.cmd`/`build.sh` wrappers "without adding logic"

Root-level files now carry more weight:

- **`global.json`** pins the SDK. Since .NET 10 it also selects the test runner and supports repo-local SDK paths (`sdk.paths`, `errorMessage`), which older hosts silently ignore ([MS Learn: global.json](https://learn.microsoft.com/en-us/dotnet/core/tools/global-json)). Its `msbuild-sdks` section pins Traversal and Arcade versions.
- **The artifacts layout** (`UseArtifactsOutput`, .NET 8+) centralizes bin and obj under `artifacts/<bin|obj|publish|package>/<Project>/<pivot>` ([MS Learn: artifacts output](https://learn.microsoft.com/en-us/dotnet/core/sdk/artifacts-output)).
- **File-based apps** (`dotnet run app.cs`, .NET 10) are attractive for repo scripts. They inherit parent `Directory.Build.props`, though, so Microsoft advises keeping them out of project trees ([MS Learn: file-based apps](https://learn.microsoft.com/en-us/dotnet/core/sdk/file-based-apps)).
- **ReferenceTrimmer** enforces reference hygiene repo-wide via `GlobalPackageReference`, flagging unnecessary project and package references (RT0002/RT0003) ([ReferenceTrimmer](https://github.com/dfederm/ReferenceTrimmer)).

**Aspire** is now the de facto dev-loop root for polyglot .NET repos. Aspire 13 (2025-11-11) made JavaScript and Python first-class and requires the .NET 10 SDK ([InfoQ](https://www.infoq.com/news/2025/11/dotnet-aspire-13-release)). **Aspire 13.4 (2026-06-01) made TypeScript AppHosts GA** and added three things relevant here ([aspire.dev 13.4](https://aspire.dev/whats-new/aspire-13-4/)):

- `aspire ls`, which discovers AppHosts
- support for pnpm and Bun
- `aspire.config.json`

An AppHost's `ProjectReference` graph is effectively a service-level dependency graph that a tool could reuse for "affected services".

### IDEs are fixing scale themselves

All three IDEs have struggled with solutions of 100 to 400+ projects. Rider issue RIDER-132952 reports its backend climbing to **41 GB** while loading projects in 2025.3 ([YouTrack](https://youtrack.jetbrains.com/projects/RIDER/issues/RIDER-132952/Runaway-memory-usage-since-upgrading-to-2025.3.0.2)). Visual Studio 2026 claims more than 50% fewer hangs ([VS blog](https://devblogs.microsoft.com/visualstudio/visual-studio-2026-is-here-faster-smarter-and-a-hit-with-early-adopters/)).

**C# Dev Kit 11.0** (pre-release, 2026-10-06) reports large load-time cuts. On Aspire's 407 projects, whole-solution load dropped from 84.1s to 3.0s. On Orleans' 155 projects it dropped from 50.3s to 2.3s, and server memory on Roslyn fell from 2,000 MB to 379 MB. These are vendor figures. Two details matter for a tool designer: **committed project-cache files can speed fresh clones and git worktrees**, and the release explicitly names parallel agentic workflows as a motivation ([.NET blog](https://devblogs.microsoft.com/dotnet/?p=60983); [Visual Studio Magazine](https://visualstudiomagazine.com/articles/2026/10/07/microsoft-rebuilds-c-dev-kit-110-for-faster-loads-lower-memory-use.aspx)).

A new tool should feed IDEs rather than compete with them: generate the right `.slnx`/`.slnf` views, and support worktree-aware caches.

## Every large repo hand-rolls its release and CI glue

### Versioning tools each solve one slice

No .NET versioning tool orchestrates a monorepo release end to end: selecting changed packages, bumping them, cascading to dependents, writing changelogs, tagging and publishing. Each tool covers a slice:

- **MinVer 7.0.0** derives versions from tags only. Its `MinVerTagPrefix` scopes a project to tags like `ext-1.0.0` ([NuGet MinVer](https://www.nuget.org/packages/MinVer)).
- **Nerdbank.GitVersioning (NBGV)** computes version height from per-directory `version.json` files with `pathFilters`. Without filters, any commit bumps every project. New commands, `nbgv path-filters check/update`, derive filters from the MSBuild project graph ([NBGV path filters](https://dotnet.github.io/Nerdbank.GitVersioning/docs/path-filters.html)).
- **GitVersion** offers only `ignore.paths` regexes (documented in PR #4420, June 2025). A commit is ignored only if *every* changed path matches ([GitVersion PR #4420](https://github.com/GitTools/GitVersion/pull/4420/files)).
- **release-please** has **no C# release type**. .NET users fall back on `simple` plus an XML XPath updater for `<Version>` ([release-please customizing](https://github.com/googleapis/release-please/blob/main/docs/customizing.md)).
- **Changesets ports** are all tiny 0.x projects: SolarWinds.Changesets v0.1.4 (about 982 downloads), Versa 0.1.0, MonoVer 0.1.3, Cxset and changepacks-csharp ([NuGet SolarWinds.Changesets](https://www.nuget.org/packages/SolarWinds.Changesets); [NuGet MonoVer](https://www.nuget.org/packages/monover); [NuGet versa](https://www.nuget.org/packages/versa)).

Whether `nx release` can version csproj or NuGet natively could not be confirmed.

### Four large repos, three archetypes

| Repo | Model | Versioning mechanism | Release trigger | Notable glue |
|---|---|---|---|---|
| opentelemetry-dotnet-contrib | Independent per component | `MinVerTagPrefix` per csproj (`Instrumentation.Http-`) | `prepare-release.yml` dispatch, then tag | `ResolveProjectForTag`, per-package `PackageValidationBaselineVersion` |
| opentelemetry-dotnet | Lockstep groups | Shared prefixes `core-`, `coreunstable-` | Tag | `ExposeExperimentalFeatures` toggled by prerelease status |
| Azure SDK for .NET | Independent, hundreds of packages | Script-edited `<Version>` + `<ApiCompatVersion>` | Per-service ADO pipeline | `Update-PkgVersion.ps1`, `-alpha.yyyyMMdd.r` dev builds |
| microsoft/aspire, Polly | Single lockstep | `eng/Versions.props` (Arcade) / one version | Tag | `bump-version.ps1`, `update-baselines.ps1` |

Sources: [OTel contrib csproj](https://github.com/open-telemetry/opentelemetry-dotnet-contrib/blob/main/src/OpenTelemetry.Instrumentation.Http/OpenTelemetry.Instrumentation.Http.csproj), [OTel contrib prepare-release.yml](https://github.com/open-telemetry/opentelemetry-dotnet-contrib/blob/main/.github/workflows/prepare-release.yml), [OTel core publish workflow](https://github.com/open-telemetry/opentelemetry-dotnet/blob/main/.github/workflows/publish-packages-1.0.yml), [Azure SDK Versioning.targets](https://github.com/Azure/azure-sdk-for-net/blob/main/eng/Versioning.targets), [Aspire Versions.props](https://github.com/microsoft/aspire/blob/main/eng/Versions.props) and [Polly eng/](https://github.com/App-vNext/Polly/tree/main/eng).

None of these repos uses conventional commits to drive version bumps; release intent is always explicit.

The hardest problem in independent versioning is **inter-package dependency pinning at pack time**. When package A has a `ProjectReference` to package B, packing A emits B's version as computed *in that build*, which may be an unpublished prerelease. A May 2026 MinVer walkthrough works around this by looking up the dependency's latest tag and passing it as `MinVerVersionOverride`, which forces "release dependency before dependent" ([startdebugging.net](https://startdebugging.net/2026/05/independently-release-multiple-nuget-packages-with-minver-and-trusted-publishing/)). This likely explains why OTel core, Polly and Aspire stay lockstep. OTel's hybrid model maps directly onto changesets-style "fixed groups" sharing one `MinVerTagPrefix`: lockstep within a group, independent across groups.

### The 2026 secure-publish pipeline has converged

The converged pipeline is: build once, sign, attest, `pack --no-build`, verify in a separate job, then push with an OIDC-minted key and `--skip-duplicate`. OTel contrib runs exactly this: cosign keyless signing, `actions/attest` on DLLs, nupkgs and the SBOM, and a separate verify job ([OTel contrib publish-packages.yml](https://github.com/open-telemetry/opentelemetry-dotnet-contrib/blob/main/.github/workflows/publish-packages.yml)).

**NuGet.org trusted publishing** exchanges an OIDC token for a **one-hour API key**.

- **Supported CI.** GitHub Actions and GitLab. It does **not** cover Azure Artifacts or private feeds.
- **Policy binding.** Policies bind to the *workflow file name*. Policies for private repos start "temporarily active for 7 days" ([MS Learn: trusted publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing)).

The file-name binding explains OTel core's comment "DON'T RENAME THIS FILE", and it argues for routing all publishing through one or a few workflow files. A July 2026 post describes moving Aspire publishing to trusted publishing. The repository it describes is not certain ([Aaron Powell](https://www.aaron-powell.com/posts/2026-07-03-switching-aspire-to-nuget-trusted-publishing/)).

Package validation needs per-package state, namely the last shipped version. OTel stores it in each csproj and errors if a baseline is missing. Azure SDK keeps `ApiCompatVersion` "managed automatically" by scripts ([OTel contrib Common.prod.props](https://github.com/open-telemetry/opentelemetry-dotnet-contrib/blob/main/build/Common.prod.props); [Azure.Storage.Blobs.csproj](https://github.com/Azure/azure-sdk-for-net/blob/main/sdk/storage/Azure.Storage.Blobs/src/Azure.Storage.Blobs.csproj)).

For deployable services, `dotnet publish /t:PublishContainer` builds OCI images without a Dockerfile. With `ContainerRegistry` set, it pushes "without using any container runtime at all" ([MS Learn: containerize with dotnet publish](https://learn.microsoft.com/dotnet/core/containers/sdk-publish)). An image release can therefore be a per-project MSBuild operation keyed on the affected set.

### CI converges on detect, gate, aggregate

GitHub's semantics force a single accepted pattern. A job skipped by `if:` "reports Success". A workflow skipped by `paths:` leaves required checks "Pending" and blocks merging ([GitHub Docs](https://docs.github.com/en/pull-requests/collaborating-with-pull-requests/collaborating-on-repositories-with-code-quality-features/troubleshooting-required-status-checks)).

OTel contrib implements the canonical shape ([OTel contrib ci.yml](https://github.com/open-telemetry/opentelemetry-dotnet-contrib/blob/main/.github/workflows/ci.yml)):

- **Detect.** A `detect-changes` job uses path filters, with one filter per component plus `build` and `shared` filters that fan out to everything.
- **Gate.** Per-component reusable workflows run only when their filter matched.
- **Aggregate.** An `if: always()` aggregator fails on any failure or cancellation and serves as the **only required check**.

Aspire layers its graph-based selector onto the same aggregator idea. Path globs are coarse and hand-maintained: any `*.props` change runs everything. Graph selection is precise but bespoke. A related lesson from 2024: GitHub Actions shallow-clones by default, so diff-based tools need `fetch-depth: 0` ([phan.nz](https://www.phan.nz/posts/doing-monorepo-dotnet/)).

On **Azure DevOps**, the Azure SDK gives each service directory its own `ci.yml` with `trigger.paths` include and exclude. Every one of them extends a shared `archetype-sdk-client.yml` and declares its release `Artifacts`, and a single PR pipeline uses `Service: auto` to detect the affected service ([sdk/storage/ci.yml](https://github.com/Azure/azure-sdk-for-net/blob/main/sdk/storage/ci.yml); [pullrequest.yml](https://github.com/Azure/azure-sdk-for-net/blob/main/eng/pipelines/pullrequest.yml)). It also balances test batches by weight with `Apply-WeightedBatching.ps1` ([Azure SDK eng/scripts](https://github.com/Azure/azure-sdk-for-net/tree/main/eng/scripts)).

All the GitHub repos examined pin actions by commit SHA, and OTel adds zizmor and OSSF Scorecard. Generated CI should do the same.

## Pain points and case studies point to a ranked opportunity map

### What practitioners actually complain about

The loudest pain is "CI does too much". In 2024, Francis Phan described a 97% C# codebase where "a single change would force us to test and deploy all the microservices". His team concluded the real problem was managing the dependency graph, not compile speed, and adopted dotnet-affected. His advice was to "adopt the monorepo pattern first, and then adopt a monorepo tool as you scale" ([phan.nz](https://www.phan.nz/posts/doing-monorepo-dotnet/)).

Remote caching comes second, once affected detection runs out. In dotnet/msbuild discussion #8718, a user writes that "msbuild doesn't provide its own cloud backed distributed cache solution". They had rejected custom tasks, nx-dotnet and BuildXL, the last for "little public documentation or support". When Microsoft pointed them to MSBuildCache, they hadn't known it existed. The thread had **5 votes and 3 participants** ([discussion #8718](https://github.com/dotnet/msbuild/discussions/8718)). The demand is latent and badly served rather than absent.

Hacker News adds two complaints:

- **Lockstep framework upgrades** in large monorepos, where upgrading .NET "requir[es] the architecture team and everything stays old" ([HN 44113973](https://news.ycombinator.com/item?id=44113973)).
- **Small and medium repos are fine without special tooling.** One developer runs a 100k-LOC C# monorepo in VS Code on a 16 GB laptop ([HN 41936489](https://news.ycombinator.com/item?id=41936489)).

Mixed JS and .NET repos slow .NET tooling too. A large `node_modules` folder slowed `.editorconfig` discovery in dotnet-format ([dotnet/format #1485](https://github.com/dotnet/format/pull/1485)). No survey quantifying .NET monorepo prevalence was found.

### Microsoft's case studies show what scale costs

Microsoft is the main public source of .NET-at-scale evidence.

**The .NET product build.** The .NET team spent "almost 4 years" (2022 to .NET 10 RTM) building the Virtual Monolithic Repo, dotnet/dotnet ([.NET blog: Reinventing how .NET builds](https://devblogs.microsoft.com/dotnet/reinventing-how-dotnet-builds-and-ships-again/)):

- **Before.** A full .NET 8/9 build "can easily run to 24" hours. One .NET 8 runtime build took 2 days 2 hours, with 38.5% overhead.
- **Targets.** The goal is under 4 hours unsigned and under 7 hours signed. **These are targets, not reported achievements.**
- **Structure.** The product builds as 35–40 single-machine "verticals" plus joins for cross-platform pieces.

**Synchronizing the VMR.** Keeping that repo in sync required custom tooling ([.NET blog: synchronizing the VMR](https://devblogs.microsoft.com/dotnet/how-we-synchronize-dotnets-virtual-monorepo/)):

- **Mechanism.** Code moves as checked-in copies shipped as binary git patches, split above git's 1 GB limit. The team rejected submodules and subtrees.
- **Problems.** A manifest that kept conflicting, and a "revert problem" that silently lost reverts.
- **Fix (December 2025).** Rebase-based flow with blocking empty PRs on conflict.

**Windows and Office.** In 2017 the Windows repo held about 300 GB and 3.5M files, serving about 4,000 engineers, and needed GVFS ([Brian Harry](https://devblogs.microsoft.com/bharry/the-largest-git-repo-on-the-planet)). Office replaced VFS for Git with Scalar plus sparse-checkout cone mode, and Scalar shipped in Git 2.38 ([GitHub blog](https://github.blog/open-source/git/the-story-of-scalar/)).

Microsoft's own conclusion is that hybrid polyrepo development with a monorepo build is expensive. Its internal answers (CloudBuild, BuildXL, Scalar, Maestro code flow) depend on dedicated infrastructure teams, and the parts made public are preview-grade or tied to Azure. **No non-Microsoft engineering case study of a large C# monorepo, or of a C# migration to Bazel or Nx, was found.** That absence is itself a gap a vendor could fill with published benchmarks.

### Opportunity map, ranked by evidence strength

| Rank | Opportunity | Evidence | Incumbent weakness |
|---|---|---|---|
| 1 | Relocatable, storage-agnostic remote cache for MSBuild (S3/GCS/Blob/HTTP), with read-only access for untrusted PRs | #8718; #9122/#6182; Nx #35934 | MSBuildCache is preview, Azure-tied, Windows-bound and has an unsafe Blob trust model; Nx's cache breaks across paths |
| 2 | Semantics-aware affected detection with audit mode, explainability, a kill switch and a non-MSBuild trigger map, extending to test-level selection | dotnet-affected's 4.55M downloads; Aspire SelectTests; paid TIA market | OSS stops at project granularity; per-test selection is paid; CPM transitive pins and deletions are undocumented |
| 3 | Release orchestration: changesets-style intent, graph-aware dependent cascades, MinVer-compatible tag prefixes, baseline management, generated trusted-publishing workflows | OTel, Azure SDK and Polly scripts; release-please lacks C#; changesets ports at 0.x | Every repo writes its own PowerShell |
| 4 | Generated CI: detect, gate and aggregate for GitHub Actions; per-unit templates for Azure DevOps; weighted sharding | OTel, Aspire and Azure SDK patterns | Hand-maintained globs |
| 5 | Solution and IDE views: graph-derived `.slnx`/`.slnf`, orphan detection, worktree and agent-aware caches | slnx default in .NET 10; Dev Kit 11 committed caches | SlnGen's `.slnx` support is still landing |
| 6 | Configuration lint: Directory.Build.* chaining, CPM policy (VersionOverride, pin floors, NU1507), test-runner consistency | fsharp #20084; `Validate-CpmCompliance.ps1` | No policy tool exists |
| 7 | Project-reference boundary rules native to MSBuild | Nx dropped `moduleBoundaries` for .NET | Little direct demand evidence |

The positioning implied by all of this is **.NET-native**: a dotnet tool or MSBuild SDK with no Node requirement. It should keep `.csproj` as the source of truth and preserve IDE and debugger fidelity. It should batch MSBuild work (`-graph`, shared evaluation) rather than spawn a process per project, and interoperate with MinVer, NBGV, Traversal and CPM rather than replace them.

Four risks deserve equal weight:

- **Microsoft could absorb features.** The persistent-evaluation-cache epic and Dev Kit's project caches are as likely to absorb parts of this opportunity as to enable it.
- **Nx could close its biggest gap.** Fixing #35934 would remove the main cache weakness.
- **Demand is concentrated in larger repos.** Small and medium repos report no pain.
- **Soundness is a hard trade-off.** Hashing from evaluation is cross-platform but misses undeclared reads, while Detours-style tracing is sound but Windows-only. Nx 22.7's I/O-tracing sandbox shows a middle path exists.

## Conclusion

The .NET monorepo gap is an integration gap, not a missing algorithm. The graph API (ProjectGraph), the cache hook (the project-cache plugin), the solution format (slnx), versioning primitives (MinVer prefixes, NBGV path filters) and the secure-publish recipe (trusted publishing plus attestations) all exist. Large repos keep writing the same few thousand lines of PowerShell and YAML to combine them. A tool that productizes that glue, essentially Aspire's SelectTests plus OTel's release and publish workflows plus a relocatable cache, would compete with in-house scripts more than with Nx or Bazel. That framing matters for adoption: such a tool can be introduced one capability at a time, starting with affected detection plus generated CI, with no build-system migration.

The timing argument is stronger than the demand data. Several platform changes landed within about a year:

- .NET 10 made slnx the default and introduced MTP via `global.json`.
- PrunePackageReference changed restore output.
- MSBuild opened a persistent-evaluation-cache epic.
- Dev Kit 11 started committing project caches for worktrees and agents.
- Nx's .NET cache is publicly broken across machines.

Together these reset many repos' build assumptions while the incumbents are weakest. The main unknowns are how large the market of 100+-project .NET repos is (no data was found) and whether Microsoft will ship a cross-platform MSBuildCache. Building on, rather than around, MSBuild's plugin and evaluation-cache APIs hedges that risk: if Microsoft improves the primitives, the tool gets faster instead of obsolete.
