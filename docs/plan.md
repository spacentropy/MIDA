# MIDA MVP plan

Status: plan, 2026-11-01. Design: [design.md](design.md). Decisions behind this plan: [cache on object storage](notes/build-cache-object-store.md), [NativeAOT deferred](notes/nativeaot.md).

## Goal

One command, `mida build`, that builds a .NET repo through MSBuild's static graph with a relocatable output cache: local disk first, Google Cloud Storage second. A second `mida build` on a clean clone at a different path is all cache hits, and a plain `dotnet build` afterwards is a no-op.

Everything else in the design (affected detection, tests, publish, metrics, coverage, CI matrix, last-green refs) stacks on the two primitives this MVP builds: the project graph and the per-project cache key. Affected detection falls out as "cache miss".

## Scope

In:

- SDK-style C# projects, one target framework per project, `ProjectReference` graphs, Central Package Management.
- `dotnet build` semantics on Linux, macOS and Windows.
- Local disk store, then GCS. Read-only mode for laptops and PRs.
- Console summary: hits, misses, time saved.

Out, named so nobody builds them by accident: multi-targeting, test running and caching, publish, OpenTelemetry, coverage, last-green refs, CI generation, S3 and Azure backends, NativeAOT, any daemon or server.

## Decisions already made

| Decision | Choice | Why |
|---|---|---|
| Packaging | Framework-dependent `dotnet tool`; MSBuild loaded from the installed SDK through MSBuildLocator | Exact fidelity with `dotnet build`; NuGet-delivered and workload SDKs work. AOT is deferred, see [nativeaot.md](notes/nativeaot.md); the rules that keep it possible are in design §1.6. |
| MSBuild integration | MSBuild project-cache plugin, registered programmatically through `BuildParameters.ProjectCacheDescriptor` on an in-process `BuildManager` graph build | Cache get has worked since MSBuild 16.9 on every platform. The project-finished hook fires without `/reportfileaccesses`, so cache add works under `dotnet build` everywhere ([spec](https://github.com/dotnet/msbuild/blob/main/documentation/specs/project-cache.md), "Cache add scenario"). No project files are touched. |
| Cache key | Single-phase hash of: predicted input file contents ([Microsoft.Build.Prediction](https://github.com/microsoft/MSBuildPrediction)), repo-local imported props and targets, dependency reference assemblies (`obj/ref`), global properties minus an ignore list, SDK version, MSBuild version. Computed in topological order | Without Detours, BuildXL's weak and strong fingerprints collapse into one. Prediction is MIT and maintained (1.2.44, October 2026). |
| Outputs | Predicted output directories scanned after the build, minus ignore patterns (`*.assets.cache`, `*AssemblyReference.cache`). Paths stored with `{RepoRoot}` and `{NugetPackageRoot}` placeholders. Target results serialized as JSON through source generators and replayed on a hit | Borrowed from [MSBuildCache](https://github.com/microsoft/MSBuildCache) (MIT). Replayed results are the spec's "most correct" hit option. |
| Restore | Not cached. `mida build` runs restore first, then the cached graph build | Same as MSBuildCache. `project.assets.json` carries absolute paths. |
| Store | `IObjectStore` from [the note](notes/build-cache-object-store.md): content-addressed blobs plus manifests, create-only writes, SHA-256 verified on read. Local filesystem implementation first | No server. No BuildXL cache libraries: they pull RocksDB native binaries and are not trim-clean. |
| Not used | BuildXL as executor, BuildXL cache libraries, MSBuildCache's Detours-based add path | Executor constraint, dependency weight, Windows-only. |

## Layout

```
src/Mida/          CLI, MSBuild hosting (Locator), project-cache plugin, key computation
src/Mida.Store/    IObjectStore, manifest model, local filesystem store, GCS store. No MSBuild dependency, AOT-clean
tests/Mida.Tests/  Microsoft.Testing.Platform (already set in global.json)
```

Conventions already in place: `Directory.Build.props` (warnings as errors, all analyzers, `IsAotCompatible`), SDK pinned in `global.json`. Only `src/Mida` may reference packages that are not trim-clean (Locator, Prediction). Keep MSBuild types out of `Mida.Store`. Code that touches Microsoft.Build types must sit in a method that runs after `MSBuildLocator.RegisterDefaults()`, otherwise the JIT binds the types before the SDK path is registered.

## Milestones

Each ends with a check that can be run. Do them in order. Milestones 1 and 2 decide details of 3 to 5.

### 1. Relocation spike (half a day)

Three chained projects (app → lib → lib). Build in checkout A, copy `bin/` and `obj/` into a clean checkout B at a different path, run `dotnet build -bl` in B and read the binlog.

Done when we know: which files must be in the manifest for CoreCompile to be skipped and for downstream projects to link; whether `Deterministic`, `ContinuousIntegrationBuild` or `PathMap` must be set (one line in a root `Directory.Build.props` is allowed by the zero-per-project-files rule); what in `obj/` must be excluded. Record the answer in `docs/notes/relocation.md`.

### 2. Plugin spike (half a day)

A `ProjectCachePluginBase` subclass that logs `GetCacheResultAsync` and `HandleProjectFinishedAsync`, driven by an in-process `BuildManager` graph build of the spike repo on macOS or Linux.

Done when: both hooks fire for every project without `/reportfileaccesses`; a hit answered with replayed target results lets the dependent project build against the restored reference assembly.

### 3. Key computation

Prediction-based inputs, hashed in topological order with dependency ref-assembly hashes.

Done when: the key is identical on two checkouts at different paths; it changes on a source edit, a package version edit, a `Directory.Build.props` edit and an SDK change; hashing costs under 10 ms per project after the first run.

### 4. Local store, end to end

`IObjectStore` plus the filesystem implementation under `~/.mida/cache`, manifest format, upload after a miss, materialize on a hit, timestamps handled.

Done when: `mida build` on a clean clone at a different path reports all hits, and `dotnet build -bl` afterwards runs no CoreCompile. A manifest whose blob is missing is a miss, not an error.

### 5. GCS store

Create-only writes with `ifGenerationMatch=0`, 412 treated as success, SHA-256 verified on read, `--read-only`. Auth through Application Default Credentials.

Done when: a hit crosses two machines, or two clones with the local tier disabled; a second writer with a different manifest for the same key is logged, not written.

### 6. MVP release

`mida build [--cache <dir|gs://bucket/prefix>] [--read-only]`, console summary, README install instructions, `dotnet tool install` from a tag.

## Known risks

- Prediction misses undeclared inputs, so the first keys are slightly unsound. Later mitigation: an extra-inputs glob setting and an audit mode that rebuilds a sample of hits and compares outputs.
- Serialized target results tie a cache entry to an MSBuild version. The MSBuild version is in the key, so an SDK bump means a cold cache, not a wrong hit.
- Version stamping (SourceLink, MinVer, GenerateAssemblyInfo) changes outputs per commit. Keep it out of the key for Build; revisit at pack and publish.
- `IsAotCompatible` only checks our own calls. Publish a throwaway AOT console that references `Mida.Store` now and then to catch dependency drift.
