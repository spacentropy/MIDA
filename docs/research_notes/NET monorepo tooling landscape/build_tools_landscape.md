# Monorepo build systems and task orchestrators for .NET (state as of 2026-10-28)

Method note: Star counts, last-push dates and latest releases come from the GitHub REST API, queried 2026-10-28 with `gh api repos/<owner>/<repo>` and `/releases`. Download counts come from the NuGet search API (`azuresearch-usnc.nuget.org/query`), queried the same day. Repo links below are the source for those numbers. Items marked "background knowledge" were not re-verified in this session.

## Q1. Nx: official @nx/dotnet plugin and legacy @nx-dotnet/core

### Takeaway
Nx is now the only mainstream polyglot monorepo tool with first-party, GA .NET support. @nx/dotnet left experimental in Nx 23 (June 2026). It infers targets from .csproj/.fsproj/.vbproj using an MSBuild-based analyzer that needs .NET SDK 8+, and it turns `ProjectReference` into graph edges. Remote caching is broken in practice, though: cached `obj/` and `bin/` outputs contain absolute paths, which also undermines Nx Agents (distributed execution) for C#. The community @nx-dotnet/core plugin is deprecated and its repo is archived.

### Cited Findings
- **Adoption and maintenance:** nrwl/nx has 29,435 stars, was last pushed 2026-10-27, and its latest release is 24.0.2 (2026-10-26). — [GitHub nrwl/nx](https://github.com/nrwl/nx)
- **GA in Nx 23 (blog post dated 2026-06-17):** "@nx/dotnet graduates out of experimental in v23". The old `@nx/dotnet/plugin` export was removed, and an automated migration rewrites nx.json. — [Nx 23 release blog](https://nx.dev/blog/nx-23-release)
- **Discovery:** Nx 23 scans `.csproj`, `.fsproj` and `.vbproj`. It infers targets with an MSBuild-based analyzer that requires .NET SDK 8.0+. — [Nx 23 release blog](https://nx.dev/blog/nx-23-release)
- **Caching:** `build`, `test`, `publish` and `pack` are cacheable, with inputs and outputs derived from MSBuild. — [Nx 23 release blog](https://nx.dev/blog/nx-23-release)
- **Graph and affected:** `<ProjectReference>` links feed the Nx graph, so `nx affected` and `nx graph` work across .NET projects. — [Nx 23 release blog](https://nx.dev/blog/nx-23-release)
- **Mixed repos:** A single task graph spans JS and .NET, with local and remote caching and CI task distribution. Scaffolding is plain `dotnet new`, with no Nx generators. — [Nx 23 release blog](https://nx.dev/blog/nx-23-release)
- **Default inferred targets and commands:**
  - `build` on all projects: `dotnet build --no-restore --no-dependencies`
  - `test` on test projects only: `dotnet test --no-build --no-restore`
  - `restore`: `dotnet restore`
  - `clean`: `dotnet clean`
  - `publish` on `OutputType=Exe` projects
  - `pack` on library projects
  - `watch` (configurable)
  - Build targets get `dependsOn: ["^build"]`. Nx, not MSBuild, therefore orchestrates cross-project ordering, and each MSBuild invocation is single-project because of `--no-dependencies`. — [Nx KB: Migrate from @nx-dotnet/core](https://nx.dev/docs/kb/migrate-from-nx-dotnet-core)
- **Test detection:** a project counts as a test project if it has `IsTestProject` or references the `Microsoft.NET.Test.Sdk` package. — [Nx KB migration](https://nx.dev/docs/kb/migrate-from-nx-dotnet-core)
- **Naming:** a project name can be overridden with `<Nx><Name>` in the .csproj. — [Nx KB migration](https://nx.dev/docs/kb/migrate-from-nx-dotnet-core)
- **Config surface:** the nx.json plugin entry has per-target `targetName` options and `watch.args`, plus an `exclude` glob array. `inferProjects: false` was removed, so projects are always inferred. — [Nx KB migration](https://nx.dev/docs/kb/migrate-from-nx-dotnet-core)
- **Dropped from @nx-dotnet/core:**
  - generators (replaced by `dotnet new` and `dotnet add`)
  - custom executors (now `command` targets)
  - `serve`, which became `watch`
  - `format`
  - `moduleBoundaries` (Nx Conformance is suggested instead)
  - NuGet version "sync" generator (replaced by Central Package Management via `Directory.Packages.props`)
  — [Nx KB migration](https://nx.dev/docs/kb/migrate-from-nx-dotnet-core)
- **@nx/dotnet requirements:** Nx 22+. The plugin is maintained in the Nx repo, and @nx-dotnet/core is deprecated. — [Nx KB migration](https://nx.dev/docs/kb/migrate-from-nx-dotnet-core)
- **Legacy community plugin:** nx-dotnet/nx-dotnet is **archived**. It has 278 stars, its last release was v3.0.2 (2025-08-22), and it was last pushed 2025-11-04. — [GitHub nx-dotnet/nx-dotnet](https://github.com/nx-dotnet/nx-dotnet)
- **Known issue #35934 (opened 2026-06-10, open):** remote cache restoration produces broken builds across machines and agents.
  - `obj/project.assets.json` and `*.nuget.g.props` embed absolute NuGet-cache and project paths.
  - A cache hit restored on a CI agent with a different working dir failed with `CS0006: Metadata file 'C:\BuildAgent\...\obj\Debug\net8.0\ref\Shared.Time.dll' could not be found`.
  - The reporter says this "makes Nx remote caching — and by extension Nx Agents / Distributed Task Execution — unusable for C# projects unless every machine happens to share identical absolute paths."
  - Commenters suggest excluding `obj/` from outputs, and also report high CPU since 22.7.
  — [nrwl/nx#35934](https://github.com/nrwl/nx/issues/35934)
- **Other open @nx/dotnet issues:**
  - [#33474](https://github.com/nrwl/nx/issues/33474) (2025-11-13): build and publish fail when `--runtime` is used.
  - [#33475](https://github.com/nrwl/nx/issues/33475) (2025-11-13): `restore` mutates `packages.lock.json` unless `--force-evaluate` is passed.
  - [#35719](https://github.com/nrwl/nx/issues/35719) (2026-05-17): the `dotnet:_build-analyzer` target fails when a folder doesn't exist.
  — [GitHub nrwl/nx issues](https://github.com/nrwl/nx/issues?q=dotnet)

### Inferences
- Because Nx runs `dotnet build --no-dependencies` per project, it relies on each project's `bin/`/`obj/` outputs from dependencies already being on disk. Cache restores must therefore be path-relocatable, and MSBuild intermediates are not, which explains #35934. A new tool should treat relocatability of `obj/` (assets file, nuget.g.props, ref assemblies) as a core design problem. Options include a fixed checkout path, a rewrite on restore, or caching only `bin/` and the ref assembly and re-running restore.
- Nx's .NET support ignores MSBuild's own incrementality and graph scheduling: one `dotnet` process per project, with JIT and MSBuild-node start-up costs. This is a likely throughput gap compared with a single `msbuild -graph -m` invocation.
- The Nx docs and blog do not mention any NuGet-aware hashing beyond what the MSBuild analyzer derives (for example, `Directory.Packages.props`). The degree of NuGet awareness is unclear.

### Gaps
- I found no published benchmarks for Nx with .NET, and no named large .NET adopters of @nx/dotnet.
- Whether Nx Cloud has any .NET-specific relocation fix as of Nx 24 is unknown. Issue #35934 is still open.
- The nx.dev docs page for .NET (nx.dev/docs/technologies/dotnet/introduction) rendered empty via fetch, so it could not be read directly.

## Q2. Bazel (rules_dotnet), Buck2, Pants, Please

### Takeaway
Bazel's rules_dotnet (bazel-contrib) is the only hermetic-build-system option for .NET that is genuinely maintained. It has a release in Aug 2026, Razor/Blazor/NativeAOT support, Paket-based NuGet via paket2bazel, and requires Bazel 9. It bypasses MSBuild entirely, so IDE support requires hand-maintained .csproj files, and adoption is small (211 stars). Buck2's prelude has only a minimal `csharp_library` that wraps `csc.exe`. Pants has no C# backend (feature request open since 2024, now stale). I found no C# rules for Please.

### Cited Findings
- **rules_dotnet adoption and release:** bazel-contrib/rules_dotnet has 211 stars and was last pushed 2026-10-28. Its latest release is v0.22.2 (2026-08-28), and it is published to the Bazel Central Registry. It has about 30 open issues and 13 open PRs. — [GitHub bazel-contrib/rules_dotnet](https://github.com/bazel-contrib/rules_dotnet)
- **Positioning:** the README presents rules_dotnet as "a alternative to using MSBuild with .Net"; the claim is "{Fast, Correct} - Choose two". — [rules_dotnet README](https://github.com/bazel-contrib/rules_dotnet)
- **Bazel version:** the minimum supported Bazel version is 9.0.0. — [rules_dotnet docs/README.md](https://github.com/bazel-contrib/rules_dotnet/blob/master/docs/README.md)
- **Windows:** requires `startup --windows_enable_symlinks` and `build --enable_runfiles`. — [rules_dotnet docs/README.md](https://github.com/bazel-contrib/rules_dotnet/blob/master/docs/README.md)
- **No MSBuild:** "MSBuild is not used behind the scenes but the compilers and tools that are part of the .Net toolchain are used directly instead." — [rules_dotnet docs](https://github.com/bazel-contrib/rules_dotnet/blob/master/docs/README.md)
- **Strict deps:** transitive dependencies are not propagated to compilation by default, equivalent to `DisableTransitiveProjectReferences=true`. The flag `--@rules_dotnet//dotnet/settings:strict_deps=false` overrides this. — [rules_dotnet docs](https://github.com/bazel-contrib/rules_dotnet/blob/master/docs/README.md)
- **Configurations:** Debug and Release map to Bazel `--compilation_mode` (dbg/fastbuild vs opt). — [rules_dotnet docs](https://github.com/bazel-contrib/rules_dotnet/blob/master/docs/README.md)
- **Supported workloads:**
  - Razor (`project_sdk = "razor"` or `"web"`), scoped CSS and static web assets
  - Blazor WebAssembly via `publish_binary(wasm=True)`, which requires net10.0+
  - NativeAOT via `publish_binary(native_aot=True)`, which needs a C/C++ toolchain (a hermetic LLVM module is recommended); Windows AOT is not yet supported
  - container images via rules_img
  — [rules_dotnet docs](https://github.com/bazel-contrib/rules_dotnet/blob/master/docs/README.md)
- **Unsupported workloads:** VB.NET, Blazor WASM AOT and relinking, and Mono-based workloads outside Blazor WASM. — [rules_dotnet docs](https://github.com/bazel-contrib/rules_dotnet/blob/master/docs/README.md)
- **NuGet:** the repo uses Paket (`paket.dependencies`, `paket.lock`). Its docs recommend Paket because it "supports a lock file out of the box" and point to paket2bazel. Example deps look like `@paket.main//microsoft.aspnetcore.components.webassembly`. — [rules_dotnet repo](https://github.com/bazel-contrib/rules_dotnet); [rules_dotnet docs (mirror)](https://github.com/bazelbuild/rules_dotnet/blob/master/docs)
- **IDE:** "Currently the rules do not support IDE support out of the box so for proper IDE support the MSBuild project files need to be manually maintained." This is from the rules_dotnet docs, read via a mirror or older copy. — [rules_dotnet docs (bazelbuild mirror)](https://github.com/bazelbuild/rules_dotnet/blob/master/docs)
- **Open issues showing gaps:**
  - [#530](https://github.com/bazel-contrib/rules_dotnet/issues/530) (2026-02): "Nuget Resolver", meaning no native NuGet resolver beyond Paket.
  - [#444](https://github.com/bazel-contrib/rules_dotnet/issues/444): support Central Package Management lock files.
  - [#436](https://github.com/bazel-contrib/rules_dotnet/issues/436): `ImplicitUsings` is not handled.
  - [#466](https://github.com/bazel-contrib/rules_dotnet/issues/466): localization.
  - [#477](https://github.com/bazel-contrib/rules_dotnet/issues/477): building .NET Framework libraries fails with CS0006.
  - [#476](https://github.com/bazel-contrib/rules_dotnet/issues/476): `nuget_repo` fetch broken after Bazel 7.2.
  - [#523](https://github.com/bazel-contrib/rules_dotnet/issues/523): type-loading problems mixing net8.0 and netstandard2.1.
  - [#584](https://github.com/bazel-contrib/rules_dotnet/issues/584) (2026-10): strong-name keys for InternalsVisibleTo.
  — [rules_dotnet issues](https://github.com/bazel-contrib/rules_dotnet/issues)
- **Older rulesets:**
  - Bazel once shipped `tools/build_defs/dotnet`, a Mono-era ruleset, in-tree. — [bazel.googlesource](https://bazel.googlesource.com/bazel/+/6f15335dea/tools/build_defs/dotnet/)
  - Brightspace/rules_csharp is a separate older ruleset; its status was not verified. — [ecosyste.ms rules_csharp](https://awesome.ecosyste.ms/projects/github.com%2Fbrightspace%2Frules_csharp)
- **Buck2:** facebook/buck2 has 4,501 stars and was pushed 2026-10-28; it ships a rolling "latest" release tag. The prelude has `prelude/csharp/`, with `csharp_library` invoking `toolchain.csc` directly (`/target:library`, `/out:`). Its `CSharpToolchainInfo` has only `csc` and `framework_dirs` fields, so it has no NuGet or SDK-style project support. — [buck2 prelude/csharp](https://github.com/facebook/buck2/tree/main/prelude/csharp)
- **Pants:** pantsbuild/pants has 3,854 stars, and its latest pre-release is 2.33.2rc3 (2026-10-23). There is no C#/dotnet backend in `src/python/pants/backend`; the list covers cc, go, java, javascript, kotlin, python, rust, scala, swift, typescript and others. Issue #21581, "Add Support for C-Sharp/C#/Dotnet" (2024-10-28), is open and labeled stale. A maintainer suggested `adhoc_tool` plus shell scripts as the workaround. — [pants#21581](https://github.com/pantsbuild/pants/issues/21581); [pants backends](https://github.com/pantsbuild/pants/tree/main/src/python/pants/backend)
- **Please:** thought-machine/please has 2,623 stars, and its latest release is v17.34.0 (2026-10-23). I found no C#/.NET rules repo in the please-build org. — [GitHub thought-machine/please](https://github.com/thought-machine/please)

### Inferences
- rules_dotnet reimplements MSBuild semantics: Razor, static web assets, ImplicitUsings (still missing), source generators, and so on. Every new SDK feature has to be re-ported, which explains the long tail of issues. A new tool that delegates to real MSBuild avoids that treadmill, at the cost of hermeticity.
- In the Bazel world, NuGet resolution is a bottleneck: Paket only, with Central Package Management lock-file support still an open proposal. Most .NET shops use CPM with `packages.lock.json`.
- Buck2, Pants and Please are effectively non-options for .NET without major custom rule work.

### Gaps
- No named production users of rules_dotnet were found. The repo's "Used by" section was empty in the capture.
- I could not read the paket2bazel docs page itself, because the gh API base64 decode of docs/paket2bazel.md failed and the file may not exist at that path.
- I found no information on a Buck2 C# toolchain for .NET Core/SDK, or whether the prelude C# rules work outside Windows.

## Q3. Microsoft BuildXL, MSBuildCache (project cache plugin), QuickBuild/CloudBuild

### Takeaway
BuildXL is open source and actively developed. It has an MSBuild frontend and runs on Windows and Ubuntu, but it is positioned as Microsoft-internal-scale infrastructure with no VS integration. MSBuildCache is the most directly usable Microsoft offering. It is a NuGet-delivered plugin for MSBuild's experimental project cache, giving project-level local and remote caching (Azure Pipelines cache, Azure Blob, 1ES Build Cache) with file-access-based fingerprinting. It is still `0.1.x-preview`, with low download counts. QuickBuild and CloudBuild have no public, usable distribution that I found.

### Cited Findings
- **BuildXL repo:** microsoft/BuildXL has 1,024 stars and was last pushed 2026-10-27. There are no GitHub Releases entries. — [GitHub microsoft/BuildXL](https://github.com/microsoft/BuildXL)
- **BuildXL at Microsoft:** it "runs 150,000+ builds per day on monorepo codebases up to a half-terabyte in size with a half-million process executions per build", with distribution to thousands of machines. — [BuildXL README](https://github.com/microsoft/BuildXL)
- **BuildXL frontends:** JavaScript, MSBuild, Ninja (CMake), and DScript. "There are currently no plans to integrate it into Visual Studio … not intended as a replacement for MSBuild." OS support is Windows 10+, Ubuntu 22.04/24.04 and Azure Linux 3. — [BuildXL README](https://github.com/microsoft/BuildXL)
- **MSBuildCache repo:** microsoft/MSBuildCache has 66 stars; its latest release is v0.1.340-preview (2026-08-28). — [GitHub microsoft/MSBuildCache](https://github.com/microsoft/MSBuildCache)
- **MSBuildCache downloads (NuGet, 2026-10-28):** — [NuGet](https://www.nuget.org/packages/Microsoft.MSBuildCache.Local)
  - Microsoft.MSBuildCache.Local: about 32K
  - .AzureBlobStorage: about 51K
  - .AzurePipelines: about 21K
- **Mechanism:** MSBuildCache provides "plugin implementations for the experimental MSBuild Project Cache, which enables project-level caching within MSBuild". It requires VS 17.9+. — [MSBuildCache README](https://github.com/microsoft/MSBuildCache)
- **Enabling it:**
  - Add a `GlobalPackageReference` to the cache package in `Directory.Packages.props`.
  - For C#, also add `Microsoft.MSBuildCache.SharedCompilation`, because the detached Roslyn compiler server's file accesses are otherwise invisible to MSBuild.
  - Run `msbuild /graph /m /reportfileaccesses`.
  — [MSBuildCache README](https://github.com/microsoft/MSBuildCache)
- **Tests:** arbitrary targets can be cached, including tests via `Microsoft.Build.RunVSTest` with `/t:Build;Test`. — [MSBuildCache README](https://github.com/microsoft/MSBuildCache)
- **Backends:**
  - **AzurePipelines:** uses Pipeline Caching and needs `SYSTEM_ACCESSTOKEN`.
  - **Local:** uses BuildXL's LocalCache.
  - **AzureBlobStorage:** the README warns it "does not yet have a robust security model … an external contributor could send a PR which would write/overwrite arbitrary content".
  - **1ES Build Cache:** referenced via `MSBuildCacheBuildCacheResourceId`.
  — [MSBuildCache README](https://github.com/microsoft/MSBuildCache)
- **Notable settings for tool designers:**
  - `MSBuildCacheIgnoredOutputPatterns` defaults to `*.assets.cache; *assemblyreference.cache`, intermediates that are "not properly portable".
  - `MSBuildCacheIgnoreDotNetSdkPatchVersion`: by default the exact SDK version is part of the fingerprint.
  - `MSBuildCacheEnableProbeAndEnumerationFingerprinting` makes file probes and directory enumerations (glob changes) part of the strong fingerprint.
  - `MSBuildCacheGlobalPropertiesToIgnore` and `MSBuildCacheTargetsToIgnore` cover information-gathering targets such as GetTargetFrameworks.
  - `MSBuildCacheGetResultsForUnqueriedDependencies`.
  - The default local cache size is 100 GB.
  — [MSBuildCache README](https://github.com/microsoft/MSBuildCache)
- **Higher-order engine:** the MSBuild static-graph spec says single-project isolated builds with `/inputResultsCaches` and `/outputResultsCache` let "a higher-order build engine" cache and distribute work. It says this is "currently only implemented internally at Microsoft". — [MSBuild static-graph spec](https://github.com/dotnet/msbuild/blob/main/documentation/specs/static-graph.md)

### Inferences
- MSBuildCache's design is the closest public reference for a correct MSBuild-native cache. It uses observed file accesses rather than declared inputs, a weak and strong fingerprint split (BuildXL two-phase cache), ignore lists for non-portable intermediates, and SDK version in the key. A new tool could reuse or emulate this.
- `/reportfileaccesses` historically relied on Windows Detours. Cross-platform availability is unverified (see Gaps) and matters for Linux CI.

### Gaps
- I could not confirm whether `/reportfileaccesses`, and therefore MSBuildCache, works on Linux or macOS with `dotnet build`. The README section fetched did not state OS support.
- I found no public QuickBuild or CloudBuild artifacts. These are Microsoft-internal (background knowledge, not verified this session).
- I found no named external adopters of MSBuildCache.

## Q4. How far plain MSBuild gets you: Traversal, NoTargets, static graph, slnx

### Takeaway
Plain MSBuild with Microsoft.Build.Traversal (dirs.proj) and `-graph` gives you graph-ordered, parallel, incremental builds, and the Traversal and NoTargets SDKs are heavily adopted (about 12.7M and 12.5M downloads). What it lacks: change-based "affected" selection, output caching (beyond the preview MSBuildCache plugin), remote execution, and non-MSBuild tasks. The static graph's `-isolate` mode is the hook for external caching and distribution engines.

### Cited Findings
- **MSBuildSdks repo:** microsoft/MSBuildSdks has 513 stars and was last pushed 2026-09-28. Microsoft.Build.Traversal is at 4.1.82 with about 12.7M downloads. Microsoft.Build.NoTargets is at 3.7.134 with about 12.5M downloads. Microsoft.Build.CentralPackageVersions, a predecessor of built-in CPM, has about 19.2M downloads. — [GitHub MSBuildSdks](https://github.com/microsoft/MSBuildSdks); [NuGet Traversal](https://www.nuget.org/packages/Microsoft.Build.Traversal); [NuGet NoTargets](https://www.nuget.org/packages/Microsoft.Build.NoTargets)
- **Traversal semantics:** Traversal "allows project tree owners the ability to define what projects should be built". It supports globs (`<ProjectReference Include="src\**\*.*proj" />`) and nested dirs.proj, so you can build from any folder. — [Traversal README](https://github.com/microsoft/MSBuildSdks/tree/main/src/Traversal)
- **`-graph`:** "msbuild will create a static graph from the entry point project and build it in topological order". `-graph -isolate` errors if an `MSBuild` task call was not known to the graph. — [MSBuild static-graph spec](https://github.com/dotnet/msbuild/blob/main/documentation/specs/static-graph.md)
- **ProjectReferenceTargets protocol:** each SDK declares a mapping from incoming target to outgoing targets, for example Build → GetTargetFrameworks, default targets, GetNativeManifest, GetCopyToOutputDirectoryItems. — [static-graph spec](https://github.com/dotnet/msbuild/blob/main/documentation/specs/static-graph.md)
- **Static graph limitations:**
  - The graph comes from evaluation-time items, making it a conservative superset.
  - Multitargeting creates speculative edges to all inner builds.
  - Cycles are disallowed.
  - I/O tracking is on hold, with tool servers and analyzers complicating it.
  - Global properties computed at runtime, such as `PublishDir`, break isolation.
  — [static-graph spec](https://github.com/dotnet/msbuild/blob/main/documentation/specs/static-graph.md)
- **dotnet/msbuild repo:** 5,552 stars, last pushed 2026-10-27. — [GitHub dotnet/msbuild](https://github.com/dotnet/msbuild)
- **slnx default:** starting with .NET 10, `dotnet new sln` creates `.slnx` by default (`--format sln` for the old format). `dotnet sln <x>.sln migrate` converts and needs SDK 9.0.200+. — [MS Learn breaking change](https://learn.microsoft.com/en-us/dotnet/core/compatibility/sdk/10.0/dotnet-new-sln-slnx-default); [Microsoft Q&A](https://learn.microsoft.com/en-us/answers/questions/5775749/how-do-i-change-the-solution-file-for-net-10)
- **SlnGen:** microsoft/slngen generates VS solutions from project graphs. It has 470 stars, and its latest release is v12.0.58 (2026-10-23). Microsoft.VisualStudio.SlnGen.Tool has about 1.63M downloads. — [GitHub microsoft/slngen](https://github.com/microsoft/slngen); [NuGet](https://www.nuget.org/packages/Microsoft.VisualStudio.SlnGen.Tool)

### Inferences
- An MSBuild-native stack is a credible "no new tool" baseline: dirs.proj, CPM, `-graph -m`, MSBuildCache, dotnet-affected to generate a traversal of affected projects, and SlnGen for IDE scoping. Its weaknesses are fragmentation (five tools, each partially maintained), no unified non-MSBuild task model (lint, docker, JS) and preview-quality caching.
- slnx is simple XML, so a new tool can easily read and write it as the IDE "view" of a monorepo subset.

### Gaps
- I found no authoritative data on how many large repos use `-graph -isolate` in production outside Microsoft.

## Q5. JS-ecosystem orchestrators (Turborepo, Rush, Lage, moon, Bit) for .NET

### Takeaway
None of Turborepo, Rush, Lage or Bit has .NET-specific integration. They can only run `dotnet` as opaque scripts inside package.json-defined workspaces, so the dependency graph has to be duplicated in package.json. moon is closest. Its v2 toolchain WASM plugin system is stable, and a community .NET toolchain plugin (Wtiben/moon-dotnet-plugin, 3 stars) exists with an open PR to the official plugins repo, but moon cannot auto-discover projects from `*.csproj` globs.

### Cited Findings
- **Turborepo:** vercel/turborepo has 31,267 stars, and its latest release is v2.11.10 (2026-10-20). It describes itself as "Build system optimized for JavaScript and TypeScript". The only dotnet-related issue found is #11114, "turbo issues with dotnet restore / dotnet publish" (closed 2025-11). — [GitHub turborepo](https://github.com/vercel/turborepo); [turborepo#11114](https://github.com/vercel/turborepo/issues/11114)
- **moon:** moonrepo/moon has 4,169 stars, and its latest release is v2.6.4 (2026-10-23). It describes itself as "for the web ecosystem". The official plugins repo has toolchains for bun, deno, go, javascript, node, python (pip/poetry/uv), ruby, rust, system and typescript, but **none for .NET**. — [GitHub moon](https://github.com/moonrepo/moon); [moonrepo/plugins toolchains](https://github.com/moonrepo/plugins/tree/master/toolchains)
- **moon issue #2447 (2026-03-26, open), "Add support for C# / .NET":**
  - The maintainer (milesj) said, "the toolchain plugin system is stable in v2", and invited community or official-repo plugins.
  - A community member built Wtiben/moon-dotnet-plugin. Its description reads: "MSBuild-evaluated project graphs, dotnet restore installs, lockfile-aware task hashing, and Docker support for SDK-style C# projects". It has 3 stars and was pushed 2026-08-20.
  - The plugin author notes "moon can't discover .NET projects on its own. `projects.globs` only matches directories or `moon.yml` files".
  - The PR to the official plugins repo, moonrepo/plugins#169 (opened 2026-07-26), is still OPEN.
  — [moon#2447](https://github.com/moonrepo/moon/issues/2447); [moonrepo/plugins#169](https://github.com/moonrepo/plugins/pull/169); [Wtiben/moon-dotnet-plugin](https://github.com/Wtiben/moon-dotnet-plugin)
- **Rush Stack:** microsoft/rushstack has 6,503 stars and was pushed 2026-10-28. A search of its issues found no dotnet/csharp integration issues. — [GitHub rushstack](https://github.com/microsoft/rushstack)
- **Lage:** microsoft/lage, "Task runner in JS monorepos", has 817 stars and was pushed 2026-10-26. It publishes no GitHub Releases, only tags like `workspace-tools_v0.42.2`. — [GitHub lage](https://github.com/microsoft/lage)
- **Bit:** teambit/bit has 18,500 stars. It is JS/TS component-focused, with no .NET support found. — [GitHub teambit/bit](https://github.com/teambit/bit)

### Inferences
- moon's gap (no discovery from project-file globs) and Nx's inferred-plugin model point to the same requirement: for .NET, the project *is* the .csproj, and a tool should discover projects from `*.csproj`, `*.slnx` and `dirs.proj` with no per-project manifest.
- Turborepo, Rush and Lage hash files and the package.json graph, so they have no knowledge of MSBuild imports (`Directory.Build.props`, `Directory.Packages.props`), which would lead to false cache hits for .NET.

### Gaps
- I did not find Gradle-for-.NET tooling. Gradle's old .NET plugins are long dead (background knowledge, not verified).
- I found no published case studies of Turborepo, Rush or Lage orchestrating .NET at scale.

## Q6. .NET-native build automation: Cake, NUKE, FAKE, Bullseye, ModularPipelines, dotnet-script

### Takeaway
These are scripting and task-DAG frameworks for writing a CI pipeline in C#/F#, not monorepo graph engines. None of the sources reviewed showed built-in project-graph discovery, affected detection, or content-addressed caching. Cake is the clear adoption leader (176M Cake.Tool downloads, v6.3.0 in Sep 2026). NUKE (41M downloads) has had no release since Dec 2025, and as of spring 2026 community reports say its primary maintainer stepped away. ModularPipelines is the actively developed newcomer.

### Cited Findings
- **Cake:** cake-build/cake has 4,198 stars. Its latest release is v6.3.0 (2026-09-14) and it was pushed 2026-10-28. Cake.Tool 6.3.0 has about 176.4M downloads. — [GitHub cake](https://github.com/cake-build/cake); [NuGet Cake.Tool](https://www.nuget.org/packages/Cake.Tool)
- **NUKE:** nuke-build/nuke has 3,749 stars. Its latest release is 10.1.0 (2025-12-02), which was also its last push, so there has been no activity for about 11 months. Nuke.Common has about 41.3M downloads. — [GitHub nuke](https://github.com/nuke-build/nuke); [NuGet Nuke.Common](https://www.nuget.org/packages/Nuke.Common)
- **NUKE maintainer status:** a blog post dated 2026-04-27 says the primary developer Matthias Koch "has reached a breaking point", describes NUKE as "effectively abandoned", and mentions rumors of a closed-source or paid fork. The author labels these rumors, and notes "the current version still works". A May 2026 Reddit thread asks "As Nuke is dead, is Cake the only alternative?", while others say it "still works pretty fine". **Unconfirmed by official announcement.** — [Bruno Massa blog](https://brunomassa-com-blog-hugo.pages.dev/en/blog/the-nuke-uncertainty-when-a-maestro-steps-away/); [Reddit snapshot](https://reddit.sentinel-team.org/posts/1tjj4hq/snapshots/2026-05-21T13%3A18%3A00.938191Z)
- **FAKE:** fsprojects/FAKE has 1,326 stars, and its latest release is 6.2.0 (2026-10-11). Fake.Core.Target has about 2.9M downloads. — [GitHub FAKE](https://github.com/fsprojects/FAKE); [NuGet](https://www.nuget.org/packages/Fake.Core.Target)
- **Bullseye:** adamralph/bullseye ("A .NET library … for running a target dependency graph") has 956 stars and was pushed 2026-10-22. Bullseye 6.3.0 has about 5.9M downloads. — [GitHub bullseye](https://github.com/adamralph/bullseye); [NuGet](https://www.nuget.org/packages/Bullseye)
- **ModularPipelines (repo stats):** thomhurst/ModularPipelines has 559 stars and was pushed 2026-10-28. ModularPipelines 3.2.8 has about 479K downloads. — [GitHub ModularPipelines](https://github.com/thomhurst/ModularPipelines); [NuGet](https://www.nuget.org/packages/ModularPipelines)
- **ModularPipelines (design):** modules declare `[DependsOn<T>]`, "ModularPipelines figures out what can run in parallel". It has DI, typed results passed between modules, and secret obfuscation, and is CI-vendor neutral. — [ModularPipelines README](https://github.com/thomhurst/ModularPipelines)
- **dotnet-script:** dotnet-script/dotnet-script has 3,011 stars, and its latest release is 2.0.1 (2026-05-28). It has about 8.4M downloads. — [GitHub dotnet-script](https://github.com/dotnet-script/dotnet-script); [NuGet](https://www.nuget.org/packages/dotnet-script)

### Inferences
- All of these model *pipeline steps* as the DAG nodes, not *projects*. Monorepo usage in practice means pairing them with dotnet-affected or Incrementalist and calling `dotnet build` on the result. This is consistent with the absence of graph features in the READMEs, but the fine-grained feature check is background knowledge.
- NUKE's uncertainty plus Cake's dominance suggest there is room for a maintained, typed C# orchestration layer. ModularPipelines is filling some of that space.

### Gaps
- I did not verify per-tool monorepo features in detail, for example Cake addins for affected detection or NUKE's `Solution` model and partitioning. Those are background knowledge.
- I found no official NUKE statement.

## Q7. Niche and .NET-specific monorepo tools

### Takeaway
The .NET-specific "affected" niche is real and active. dotnet-affected (4.6M downloads, v7.0.1 Oct 2026) walks the MSBuild graph from git changes, handles CPM and imported props, and emits a Traversal project or solution filter. Incrementalist (Petabridge, about 576K downloads, v1.2.2 Aug 2026) does the same with libgit2sharp and Roslyn. Neither caches outputs or distributes work.

### Cited Findings
- **dotnet-affected (stats):** leonardochaia/dotnet-affected has 413 stars. Its latest release is v7.0.1 (2026-10-07), and the dotnet-affected tool has about 4.55M downloads. — [GitHub dotnet-affected](https://github.com/leonardochaia/dotnet-affected); [NuGet](https://www.nuget.org/packages/dotnet-affected)
- **dotnet-affected (features):**
  - It "compares your working tree against a baseline commit, maps the changed files to the projects that reference them, and walks the MSBuild project graph".
  - It detects changes in any referenced file, NuGet changes under CPM, and `Directory.Build.props`/`.targets` imports.
  - Output can be a Traversal `affected.proj`, a solution filter, text or JSON.
  - It supports csproj, fsproj and vbproj, both SDK and non-SDK.
  - It exits with code `166` when nothing changed.
  - It also ships an MSBuild SDK (DotnetAffected.Tasks) and a GitHub Action, and has docs at dotnet-affected.com.
  — [dotnet-affected README](https://github.com/leonardochaia/dotnet-affected)
- **Incrementalist (stats):** petabridge/Incrementalist has 346 stars. Its latest release is 1.2.2 (2026-08-18), and Incrementalist.Cmd has about 576K downloads. — [GitHub Incrementalist](https://github.com/petabridge/Incrementalist); [NuGet](https://www.nuget.org/packages/Incrementalist.Cmd)
- **Incrementalist (features):** it uses libgit2sharp and Roslyn to compute affected projects and requires .NET 8 SDK and git. It has the verbs `run` (forwards args to `dotnet`), `--dry`, `list-affected-folders`, `--target-glob` and `--parallel`. — [Incrementalist README](https://github.com/petabridge/Incrementalist)
- **SlnGen:** see Q4, 1.63M downloads. — [microsoft/slngen](https://github.com/microsoft/slngen)
- **Community skill guidance:** at least one widely republished community "dotnet project structure" agent skill (aaronontheweb/dotnet-skills) recommends slnx and CPM layouts. — [playbooks.com listing](https://playbooks.com/skills/aaronontheweb/dotnet-skills/project-structure)

### Inferences
- The existence and steady downloads of dotnet-affected and Incrementalist show the demand: affected detection is the most-requested monorepo feature in .NET. Both stop at selection and hand off to `dotnet build`. Caching, remote execution and a unified task model are the open space.

### Gaps
- GitHub repo searches for other terms ("dotnet monorepo build tool", "dotnet build cache remote msbuild", "slnx monorepo") returned no results via `gh search repos`. I may have missed smaller tools (for example "dotnet-monorepo"-named repos). I found no reliable source for a tool literally named "dotnet-monorepo".

## Q8. Comparison table (for .NET specifically)

### Takeaway
No tool combines MSBuild-faithful graph discovery, CPM/NuGet-aware hashing, relocatable remote cache, distributed execution and IDE-native workflow. Nx comes closest on orchestration but breaks on cache relocatability. Bazel comes closest on correctness and remote execution but abandons MSBuild and the IDE. MSBuild plus MSBuildCache is the most faithful but is preview-quality, tied to Azure, and has no affected detection.

### Cited Findings
Cells summarize the findings above, with sources cited in Q1–Q7. "n/a" means no capability found. "Script" means `dotnet` is invoked as an opaque command.

| Tool | Graph discovery | Affected | Local cache | Remote cache | Distributed exec | NuGet awareness | IDE story | Learning curve | Status (last release) |
|---|---|---|---|---|---|---|---|---|---|
| Nx + @nx/dotnet | Inferred from csproj/fsproj/vbproj via MSBuild analyzer (SDK 8+) | Yes (`nx affected`) | Yes | Nx Cloud, but cross-machine restores broken (#35934) | Nx Agents (same caveat) | Via MSBuild-derived inputs; CPM recommended | Plain csproj, IDE unaffected | Medium (Node/npm required) | Active; 24.0.2 (2026-10-26) |
| @nx-dotnet/core | csproj | Yes | Yes | Yes | Yes | sync generator | Plain csproj | Medium | Archived; v3.0.2 (2025-08) |
| Bazel + rules_dotnet | Hand-written BUILD files, no MSBuild | Via bazel query | Yes | Yes (REAPI) | Yes (RBE) | Paket via paket2bazel; no CPM-lock support (#444) | Must maintain csproj manually | High | Active; v0.22.2 (2026-08-28) |
| Buck2 | Hand-written; minimal csc wrapper | Via query | Yes | Yes | Yes | n/a | n/a | High | Prelude only |
| Pants / Please | n/a (no C# backend) | n/a | n/a | n/a | n/a | n/a | n/a | n/a | Open request (Pants #21581) |
| BuildXL (MSBuild frontend) | MSBuild static graph | n/a found | Yes | Yes | Yes (internal scale) | n/a found | No VS integration | High | Active repo; no GH releases |
| MSBuild + MSBuildCache | MSBuild `-graph` | No | Yes (BuildXL LocalCache) | Azure Pipelines, Azure Blob (insecure for PRs), 1ES | No | SDK version in fingerprint; file-access tracking | Native | Low–Medium | Preview; v0.1.340 (2026-08-28) |
| MSBuild + Traversal/NoTargets | dirs.proj globs | No | MSBuild incremental only | No | No | CPM native | Native | Low | Active; Traversal 4.1.82 |
| dotnet-affected | MSBuild graph from git diff | Yes (CPM-aware) | No | No | No | CPM changes detected | Emits .slnf/traversal | Low | Active; v7.0.1 (2026-10-07) |
| Incrementalist | Roslyn/libgit2 | Yes | No | No | No | n/a found | n/a | Low | Active; 1.2.2 (2026-08-18) |
| moon + community dotnet plugin | MSBuild-evaluated graph, but no csproj glob discovery | Yes | Yes | moonbase/self-host | n/a | Lockfile-aware hashing (claimed) | Plain csproj | Medium | Plugin 3 stars; PR unmerged |
| Turborepo / Rush / Lage / Bit | package.json only | JS graph only | Script | Script | n/a | None | n/a | Medium | No .NET integration |
| Cake / NUKE / FAKE / Bullseye / ModularPipelines | Pipeline-step DAG, not project graph | No (pair with dotnet-affected) | No | No | No | n/a | C# IDE for scripts | Low | Cake 6.3.0 (2026-09); NUKE 10.1.0 (2025-12, uncertain); FAKE 6.2.0 (2026-10) |

### Inferences
- Design levers for a new .NET monorepo tool:
  1. Discover projects from MSBuild directly (csproj, slnx, dirs.proj) with zero per-project config.
  2. Hash using MSBuild-evaluated inputs, including imports, CPM and SDK version, ideally plus file-access tracing like MSBuildCache.
  3. Make cached outputs relocatable: handle `obj/project.assets.json`, `nuget.g.props` and ref assemblies, which is the Nx #35934 failure.
  4. Batch MSBuild invocations (`-graph`, `/inputResultsCaches`) rather than spawning one process per project.
  5. Keep the IDE working on real csproj and generate slnx/slnf views via an SlnGen-like step.
  6. Offer non-Azure remote cache backends with a PR-safe trust model, which MSBuildCache Blob lacks.

### Gaps
- Several cells marked "n/a found" reflect the absence of evidence in sources reviewed, not confirmed absence.
- I found no benchmarks comparing these tools on a common .NET monorepo.
