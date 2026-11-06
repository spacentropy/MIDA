# MIDA MVP plan

Status: plan, 2026-11-01, updated 2026-11-06 after milestones 1 to 3. Design: [design.md](design.md). Decisions behind this plan: [cache on object storage](notes/build-cache-object-store.md), [NativeAOT deferred](notes/nativeaot.md), [relocation](notes/relocation.md), [project-cache plugin](notes/project-cache-plugin.md), [cache key](notes/cache-key.md).

## Goal

One command, `mida build`, that builds a .NET repo through MSBuild's static graph with a relocatable output cache: local disk first, Google Cloud Storage second. A second `mida build` on a clean clone at a different path is all cache hits, and `dotnet test --no-build` runs on the result. The target is CI build and test time. Local incremental builds already work, so making a plain `dotnet build` a no-op afterwards is not a goal.

Everything else in the design (affected detection, tests, publish, metrics, coverage, CI matrix, last-green refs) stacks on the two primitives this MVP builds: the project graph and the per-project cache key. Affected detection falls out as "cache miss".

## Current state

Milestones 1 to 3 are done. `mida build [solution]` restores, evaluates the static graph, predicts inputs, injects the two properties, and runs an in-process graph build whose plugin computes a key for every project. **Nothing is stored or restored yet, so every project is a miss** and no key is read back.

Designed but not built: `Mida.Store`, the manifest, target-result replay, dependency references and copy-on-write materialization. Replay and the overlay behind dependency references were proven in throwaway spikes, whose code was not kept. [project-cache-plugin.md](notes/project-cache-plugin.md) records the API calls.

Diagnostics today: `--print-keys`, `--key-details <dir>` to write each key's lines, and `MIDA_BINLOG=<path>` for a binlog. Tests: `dotnet test --solution MIDA.slnx`, 35 across both target frameworks, including real builds of a small repo.

Everything so far ran on macOS arm64 only. CI is the target and is likely Linux.

## Scope

In:

- SDK-style C# projects, one target framework per project, `ProjectReference` graphs, Central Package Management.
- `dotnet build` semantics on Linux, macOS and Windows.
- .NET SDK 10 and 11 only, the supported versions. Projects may target any framework those SDKs can build.
- Local disk store, then GCS. Read-only mode for laptops and PRs. Platform is in the key, so a laptop only hits entries written on its own OS and architecture.
- Console summary: hits, misses, time saved.

Next after the MVP: test running and caching, the other half of CI time. Test keys must hash dependency implementations, not reference assemblies.

Later: selective NuGet caching from the bucket, fetching only the packages that projects missing the cache need. A cold restore takes 15.7 s on the test repo against 1.8 s with packages present, the largest part of a hit on a fresh runner. Until then CI caches `~/.nuget/packages` itself. The catch: the key reads `project.assets.json`, and restore needs every package to write it, so a selective fetch needs another source for resolved versions, such as `packages.lock.json`.

Out, named so nobody builds them by accident: multi-targeting, publish, OpenTelemetry, coverage, last-green refs, CI generation, S3 and Azure backends, NativeAOT, any daemon or server.

## Decisions already made

| Decision | Choice | Why |
|---|---|---|
| Packaging | Framework-dependent `dotnet tool` multi-targeting `net10.0;net11.0`; MSBuild loaded from the installed SDK through MSBuildLocator. A host cannot load a newer SDK's MSBuild, so a CI image with only SDK 10 needs the `net10.0` build | Exact fidelity with `dotnet build`; NuGet-delivered and workload SDKs work. AOT is deferred, see [nativeaot.md](notes/nativeaot.md); the rules that keep it possible are in design §1.6. |
| MSBuild integration | MSBuild project-cache plugin, registered programmatically through `BuildParameters.ProjectCacheDescriptor` on an in-process `BuildManager` graph build | Cache get has worked since MSBuild 16.9 on every platform. The project-finished hook fires without `/reportfileaccesses`, so cache add works under `dotnet build` everywhere ([spec](https://github.com/dotnet/msbuild/blob/main/documentation/specs/project-cache.md), "Cache add scenario"). No project files are touched. The API is `Microsoft.Build.ProjectCache`, identical in SDK 10 and 11. |
| Cache key | Single-phase hash of: predicted input file contents ([Microsoft.Build.Prediction](https://github.com/microsoft/MSBuildPrediction)), repo-local imported props and targets, resolved packages from `project.assets.json`, transitive dependency reference assemblies (`obj/ref`), the full key of build-time dependencies such as source generators, global properties minus an ignore list, SDK version, MSBuild version, platform. Computed during the build, in topological order. See [cache-key.md](notes/cache-key.md) | Without Detours, BuildXL's weak and strong fingerprints collapse into one. Prediction is MIT and maintained (1.2.44, October 2026). |
| Outputs | Predicted output directories scanned after the build, minus the exclusions in [relocation.md](notes/relocation.md). File contents are not rewritten and timestamps are left alone. Manifest paths stay relative to the repo root, and replayed target results carry `{RepoRoot}`. Files in `bin/` that came from a project dependency are stored as references and resolved to its current file on every hit, because the ref-based key does not cover them ([why](notes/project-cache-plugin.md#stale-dependency-copies)). Target results serialized as JSON through source generators and replayed on a hit | Borrowed from [MSBuildCache](https://github.com/microsoft/MSBuildCache) (MIT). Replayed results are the spec's "most correct" hit option. |
| Injected properties | `mida build` always passes `ContinuousIntegrationBuild=true` and `IncludeSourceRevisionInInformationalVersion=false` as global properties, on CI and laptops. Nothing is added to the repo | Portable PDB paths and byte-identical outputs; a commit SHA in the reference assembly would defeat early cutoff. Global properties are in the key, so laptops must match CI to get hits. See [relocation.md](notes/relocation.md). |
| Restore | Not cached. `mida build` runs restore first, then the cached graph build | Same as MSBuildCache. `project.assets.json` carries absolute paths. |
| Store | `IObjectStore` from [the note](notes/build-cache-object-store.md): content-addressed blobs plus manifests, create-only writes, SHA-256 verified on read. Local filesystem implementation first | No server. No BuildXL cache libraries: they pull RocksDB native binaries and are not trim-clean. |
| Failures | A key that cannot be computed, or any store failure, is a miss with a warning. It never fails the build | The cache must not become a new way to break CI. An exception that escapes a plugin hook does fail the build, so the plugin catches them. |
| Not used | BuildXL as executor, BuildXL cache libraries, MSBuildCache's Detours-based add path | Executor constraint, dependency weight, Windows-only. |

## Layout

```
src/Mida/          CLI, MSBuild hosting (Locator), project-cache plugin, key computation
src/Mida.Store/    IObjectStore, manifest model, local filesystem store, GCS store. No MSBuild dependency, AOT-clean. Not created yet: milestone 4
tests/Mida.Tests/  xUnit v3 on Microsoft.Testing.Platform (set in global.json)
MIDA.slnx          Both projects
```

Conventions already in place: `Directory.Build.props` (warnings as errors, all analyzers, `IsAotCompatible`, `net10.0;net11.0`, `runtime-async` on `net11.0` only), SDK pinned in `global.json`. Only `src/Mida` may reference packages that are not trim-clean (Locator, Prediction). Keep MSBuild types out of `Mida.Store`. MSBuild packages compile against 18.9.6, the oldest supported API, with `ExcludeAssets="runtime" PrivateAssets="all"` so the SDK's copies load at run time; Prediction brings `Microsoft.NET.StringTools` in, so that one needs the same treatment. Code that touches Microsoft.Build types must sit in a method that runs after `MSBuildLocator.RegisterDefaults()`, otherwise the JIT binds the types before the SDK path is registered.

## Milestones

Each ends with a check that can be run. Do them in order. Milestones 1 and 2 decide details of 3 to 5.

### 1. Relocation spike (half a day)

Done 2026-11-06, see [relocation.md](notes/relocation.md). The root `Directory.Build.props` line below was superseded by injected properties.

Three chained projects (app → lib → lib). Build in checkout A, copy `bin/` and `obj/` into a clean checkout B at a different path, run `dotnet build -bl` in B and read the binlog.

Done when we know: which files must be in the manifest for CoreCompile to be skipped and for downstream projects to link; whether `Deterministic`, `ContinuousIntegrationBuild` or `PathMap` must be set (one line in a root `Directory.Build.props` is allowed by the zero-per-project-files rule); what in `obj/` must be excluded. Record the answer in `docs/notes/relocation.md`.

### 2. Plugin spike (half a day)

Done 2026-11-06, see [project-cache-plugin.md](notes/project-cache-plugin.md).

A `ProjectCachePluginBase` subclass that logs `GetCacheResultAsync` and `HandleProjectFinishedAsync`, driven by an in-process `BuildManager` graph build of the spike repo on macOS or Linux.

Done when: both hooks fire for every project without `/reportfileaccesses`; a hit answered with replayed target results lets the dependent project build against the restored reference assembly, with no refresh pass and no timestamp changes; the two injected properties reach every project.

### 3. Key computation

Done 2026-11-06, see [cache-key.md](notes/cache-key.md). About 8 ms per project with no on-disk hash cache, so the "after the first run" allowance was not needed.

Prediction-based inputs, hashed in topological order with dependency ref-assembly hashes.

Done when: the key is identical on two checkouts at different paths; it changes on a source edit, a package version edit, a `Directory.Build.props` edit and an SDK change; hashing costs under 10 ms per project after the first run.

### 4. Local store, end to end

`IObjectStore` plus the filesystem implementation under `~/.mida/cache`, manifest format including dependency references, upload after a miss, materialize on a hit using copy-on-write where the filesystem has it. Blobs are per file: outputs on the test repo dedupe 7.9x ([relocation.md](notes/relocation.md)). Replay target results for every requested target, with `{RepoRoot}` restored. Normalize predicted output directories, which came back with mixed separators on macOS (`obj\Debug/net8.0/`).

Done when: `mida build` on a clean clone at a different path reports all hits, and `dotnet test --no-build` passes on the result. After a method body edit in a library, a test covering that code fails, both when the library misses and when it hits from a newer entry than its dependents. The same holds for a file the library copies to output, such as `appsettings.json`. A manifest whose blob is missing is a miss, not an error. All of it also passes on Linux.

### 5. GCS store

Create-only writes with `ifGenerationMatch=0`, 412 treated as success, SHA-256 verified on read, `--read-only`. Auth through Application Default Credentials. Replayed target results may hold package paths, so add a `{NugetPackageRoot}` token if they do.

Done when: a hit crosses two machines, or two clones with the local tier disabled, with different NuGet roots; a second writer with a different manifest for the same key is logged, not written.

### 6. MVP release

`mida build [--cache <dir|gs://bucket/prefix>] [--read-only]`, console summary, README install instructions and CI usage (the injected properties, `--no-build` on every later step, caching `~/.nuget/packages` in CI), a clear error when the loaded SDK is not the one `global.json` pins, `dotnet tool install` from a tag.

## Known risks

- Everything so far ran on macOS arm64 only, in Debug. Linux, Windows and Release builds are unproven, and Linux is the likely CI target.
- The plugin API was experimental until recently and moved namespace between SDK 8 and 10, so a future SDK can break it. The fallback is driving `dotnet build` per project ourselves; keys and the store would not change.
- Real source generators are untested: the build-time dependency rule was checked with a plain library.
- Multi-targeted projects get no key and build normally, so they gain nothing and their dependents still work.
- Environment variables are not in the key, so one that changes build output gives a false hit.
- Prediction misses undeclared inputs, so the first keys are slightly unsound. Later mitigation: an extra-inputs glob setting and an audit mode that rebuilds a sample of hits and compares outputs.
- Serialized target results tie a cache entry to an MSBuild version. The MSBuild version is in the key, so an SDK bump means a cold cache, not a wrong hit.
- Version stamping (SourceLink, MinVer, GenerateAssemblyInfo) changes outputs per commit. Keep it out of the key for Build; revisit at pack and publish. The default commit SHA in `AssemblyInformationalVersion` also reaches the reference assembly, so it must stay off during Build ([relocation.md](notes/relocation.md)). A tool like MinVer that stamps a per-commit version would need the same check.
- A missed dependency reference means tests run against stale code and pass. Every change to the manifest or key must keep the milestone 4 stale check.
- A plain `dotnet build` or `dotnet test` after `mida build` recompiles everything (49 of 49 on the test repo), so a CI step that forgets `--no-build` silently loses the saving. If local no-op builds become a goal, the recipe is in [relocation.md](notes/relocation.md).
- A plain copy of outputs takes about as long as compiling them (18.9 s against 27.7 s on the test repo). On filesystems without copy-on-write, such as ext4, a local hit may save little.
- Outputs of `mida build` carry `/_/` source paths, which may hurt debugging them locally. Untested. Plain `dotnet build` is unaffected.
- `IsAotCompatible` only checks our own calls. Publish a throwaway AOT console that references `Mida.Store` now and then to catch dependency drift.
