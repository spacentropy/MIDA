# MIDA MVP plan

Status: plan, 2026-11-01, updated 2026-11-08 after milestones 1 to 5, an adversarial review and 4b items 1 and 2. **Next: [milestone 6](#6-mvp-release), plus the decision in 4b item 3 and the real-repo run in 4b item 4.** Design: [design.md](design.md). Decisions behind this plan: [cache on object storage](notes/build-cache-object-store.md), [NativeAOT deferred](notes/nativeaot.md), [relocation](notes/relocation.md), [project-cache plugin](notes/project-cache-plugin.md), [cache key](notes/cache-key.md), [local store](notes/local-store.md), [benchmarks and test harness](notes/benchmarks.md).

## Goal

One command, `mida build`, that builds a .NET repo through MSBuild's static graph with a relocatable output cache: local disk first, Google Cloud Storage second. A second `mida build` on a clean clone at a different path is all cache hits, and `dotnet test --no-build` runs on the result. The target is CI build and test time. Local incremental builds already work, so making a plain `dotnet build` a no-op afterwards is not a goal.

Everything else in the design (affected detection, tests, publish, metrics, coverage, CI matrix, last-green refs) stacks on the two primitives this MVP builds: the project graph and the per-project cache key. Affected detection falls out as "cache miss".

## Current state

Milestones 1 to 5 are done, and so are items 1 and 2 of milestone 4b and the profiling step of item 4. Item 3 and the real-repo measurement of item 4 are open. `mida build [solution] [-c <configuration>] [-p:<name>=<value>] [--cache gs://<bucket>[/<prefix>]] [--read-only]` restores, evaluates the static graph, predicts inputs, injects the two properties, and runs an in-process graph build whose plugin computes a key for every project, restores a hit from the local store or through it from GCS, and stores a miss in both. A clean clone at another path is all hits and `dotnet test --no-build` passes on it. See [local-store.md](notes/local-store.md).

Designed but not built: the summary's time saved.

Diagnostics today: `--cache <dir>` to use a local store other than `~/.mida/cache`, `--print-keys`, `--key-details <dir>` to write each key's lines, and `MIDA_BINLOG=<path>` for a binlog. Tests: `dotnet test --project tests/Mida.Tests -f net10.0` (and `-f net11.0`; without `-f` the command fails on a path bug), 104 on net10.0 and 105 on net11.0, including real builds and real test runs of a small repo and of a generated 8-project graph. The GCS tests start fake-gcs-server in Docker and skip without it. Benchmarks on generated 8, 128 and 1024-project repos are `[Explicit]` tests, see [benchmarks.md](notes/benchmarks.md). After a failed build, `--no-build` runs stale binaries, so check the build first.

The cache has run on a five-project repo on macOS arm64 and Linux arm64. It has not run on the 49-project repo yet, so the 3 s hit is a sum of spike timings, not a measurement.

## Scope

In:

- SDK-style C# projects, one target framework per project, `ProjectReference` graphs, Central Package Management.
- `dotnet build` semantics on Linux, macOS and Windows.
- .NET SDK 10 and 11 only, the supported versions. Projects may target any framework those SDKs can build.
- Local disk store, then GCS. Read-only mode for PR runners. MIDA is a CI tool: laptops run it only to develop MIDA itself, so laptop hits on CI entries are not a goal. Platform and the exact SDK patch are in the key anyway, so a Mac would never hit Linux CI entries.
- Console summary: hits, misses, time saved.

Next after the MVP: test running and caching, the other half of CI time. Test keys must hash dependency implementations, not reference assemblies. The generated repo in [benchmarks.md](notes/benchmarks.md) is the oracle: after an edit, `mida test` must run exactly the test projects in the dependent closure.

Later: selective NuGet caching from the bucket, fetching only the packages that projects missing the cache need. A cold restore takes 15.7 s on the test repo against 1.8 s with packages present, the largest part of a hit on a fresh runner. Until then CI caches `~/.nuget/packages` itself. The catch: the key reads `project.assets.json`, and restore needs every package to write it, so a selective fetch needs another source for resolved versions, such as `packages.lock.json`.

Out, named so nobody builds them by accident: caching multi-targeted projects themselves (their dependents must still get keys, see 4b), publish, OpenTelemetry, coverage, last-green refs, CI generation, S3 and Azure backends, NativeAOT, any daemon or server.

## Decisions already made

| Decision | Choice | Why |
|---|---|---|
| Packaging | Framework-dependent `dotnet tool` multi-targeting `net10.0;net11.0`; MSBuild loaded from the installed SDK through MSBuildLocator. A host cannot load a newer SDK's MSBuild, so a CI image with only SDK 10 needs the `net10.0` build | Exact fidelity with `dotnet build`; NuGet-delivered and workload SDKs work. AOT is deferred, see [nativeaot.md](notes/nativeaot.md); the rules that keep it possible are in design §1.6. |
| MSBuild integration | MSBuild project-cache plugin, registered programmatically through `BuildParameters.ProjectCacheDescriptor` on an in-process `BuildManager` graph build | Cache get has worked since MSBuild 16.9 on every platform. The project-finished hook fires without `/reportfileaccesses`, so cache add works under `dotnet build` everywhere ([spec](https://github.com/dotnet/msbuild/blob/main/documentation/specs/project-cache.md), "Cache add scenario"). No project files are touched. The API is `Microsoft.Build.ProjectCache`, identical in SDK 10 and 11. |
| Cache key | Single-phase hash of: predicted input file contents ([Microsoft.Build.Prediction](https://github.com/microsoft/MSBuildPrediction)), repo-local imported props and targets, resolved packages from `project.assets.json`, transitive dependency reference assemblies (`obj/ref`), the full key of build-time dependencies such as source generators, global properties minus an ignore list, SDK version, MSBuild version, platform. Computed during the build, in topological order. See [cache-key.md](notes/cache-key.md) | Without Detours, BuildXL's weak and strong fingerprints collapse into one. Prediction is MIT and maintained (1.2.44, October 2026). |
| Outputs | Predicted output directories scanned after the build, minus the exclusions in [relocation.md](notes/relocation.md). File contents are not rewritten and timestamps are left alone. Manifest paths stay relative to the repo root, and replayed target results carry `{RepoRoot}`. Files in `bin/` that came from a project dependency are stored as references and resolved to its current file on every hit, because the ref-based key does not cover them ([why](notes/project-cache-plugin.md#stale-dependency-copies)). Target results serialized as JSON through source generators and replayed on a hit | Borrowed from [MSBuildCache](https://github.com/microsoft/MSBuildCache) (MIT). Replayed results are the spec's "most correct" hit option. |
| Injected properties | `mida build` always passes `ContinuousIntegrationBuild=true` and `IncludeSourceRevisionInInformationalVersion=false` as global properties on every run. Nothing is added to the repo | Portable PDB paths and byte-identical outputs; a commit SHA in the reference assembly would defeat early cutoff. See [relocation.md](notes/relocation.md). |
| Restore | Not cached. `mida build` runs restore first, then the cached graph build | Same as MSBuildCache. `project.assets.json` carries absolute paths. |
| Store | `IObjectStore` from [the note](notes/build-cache-object-store.md), cut to three methods, where a restore takes a path so a filesystem store can clone: content-addressed blobs plus manifests, create-only writes, SHA-256 verified on read, once per blob per run. Local filesystem implementation first | No server. No BuildXL cache libraries: they pull RocksDB native binaries and are not trim-clean. |
| Failures | A key that cannot be computed, or any store failure, is a miss with a warning. It never fails the build | The cache must not become a new way to break CI. An exception that escapes a plugin hook does fail the build, so the plugin catches them. |
| Not used | BuildXL as executor, BuildXL cache libraries, MSBuildCache's Detours-based add path | Executor constraint, dependency weight, Windows-only. |

## Layout

```
src/Mida/          CLI, MSBuild hosting (Locator), project-cache plugin, key computation
src/Mida.Store/    IObjectStore, manifest model, restore and save, local filesystem store, GCS store. No MSBuild dependency, AOT-clean
tests/Mida.Tests/  TUnit on Microsoft.Testing.Platform (set in global.json)
MIDA.slnx          All three projects
```

Conventions already in place: `Directory.Build.props` (warnings as errors, all analyzers, `IsAotCompatible`, `net10.0;net11.0`, `runtime-async` on `net11.0` only), SDK pinned in `global.json`. Tests use TUnit only, including test projects that fixtures generate; the test project is the one place `IsAotCompatible` is off, because it never ships. Only `src/Mida` may reference packages that are not trim-clean (Locator, Prediction). Keep MSBuild types out of `Mida.Store`. MSBuild packages compile against 18.9.6, the oldest supported API, with `ExcludeAssets="runtime" PrivateAssets="all"` so the SDK's copies load at run time; Prediction brings `Microsoft.NET.StringTools` in, so that one needs the same treatment. Code that touches Microsoft.Build types must sit in a method that runs after `MSBuildLocator.RegisterDefaults()`, otherwise the JIT binds the types before the SDK path is registered.

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

Done 2026-11-07, see [local-store.md](notes/local-store.md). All of it passes on macOS arm64 and Linux arm64, against a five-project repo, with `dotnet test --no-build` running real tests on the hits. Not yet run on the 49-project repo, so its 3 s hit is unmeasured; that is part of 4b. The toy repo the tests generate is never committed to git, so source-path mapping is off in every automated check, and only the hand-run spike on the real repo covered it.

Changes from the plan:

- `File.Copy` clones, so no P/Invoke was needed, and `IObjectStore` takes paths.
- Copied files reach dependents through the key, not references.
- A new satellite assembly needed a `dependency-files` key line.
- `*.CoreCompileInputs.cache` and `*.GeneratedMSBuildEditorConfig.editorconfig` are left out, so manifests are byte-identical across checkouts of one commit.
- After review: a hit deletes outputs its manifest does not list and a miss clears them first, manifests are validated before any file moves, and read-only files are replaceable on Windows.
- A project with static web assets is not stored, because its manifests name the checkout. On SDK 11 that is every Web SDK project. The 49-project repo has five, so measure what that costs when it runs, and relocate these manifests if the cost is real.

The test suite also moved from xUnit v3 to TUnit, and the stale-copy mutation checks were repeated on it.

`IObjectStore` plus the filesystem implementation under `~/.mida/cache`, manifest format including dependency references, upload after a miss, materialize on a hit using copy-on-write where the filesystem has it. Blobs are per file: outputs on the test repo dedupe 7.9x ([relocation.md](notes/relocation.md)). Replay target results for every requested target, with `{RepoRoot}` restored. Normalize predicted output directories, which came back with mixed separators on macOS (`obj\Debug/net8.0/`).

Done when: `mida build` on a clean clone at a different path reports all hits, and `dotnet test --no-build` passes on the result. After a method body edit in a library, a test covering that code fails, both when the library misses and when it hits from a newer entry than its dependents. The same holds for a file the library copies to output, such as `appsettings.json`. A manifest whose blob is missing is a miss, not an error. All of it also passes on Linux.

### 4b. Fixes from the adversarial review

Added 2026-11-07, not started. Two reviews of the finished milestone 4 by a second model. They could not break relocation, key coverage or stale-copy handling: inputs such as `.editorconfig`, `global.json`, `Directory.Build.targets` and out-of-tree `Compile` items are in the key, manifests are byte-identical across checkouts, and `UseArtifactsOutput` hits with current dependency copies. What they did find is below, in order. Items 1 and 2 are code. Item 3 is a decision. Item 4 is the measurement milestone 4 skipped.

1. **Done 2026-11-08.** A dependency that is an outer node now adds the lines of every inner node, so its dependents get keys. Resolving the one framework the dependent picks was not needed. `DependencyCopies` already worked, because the closure reaches the inner nodes. The end-to-end tests run the clean clone and both stale checks a second time with `LibB` on `net10.0;netstandard2.0`. A `netstandard2.0` project has no reference assembly unless `ProduceReferenceAssembly` is set, so the key hashes its implementation, and any body edit misses every dependent. That holds for single-target `netstandard2.0` projects too, and outside git that implementation names the checkout, so the test sets the property.

   The finding: **One multi-targeted library turns the cache off above it.** For a dependency that is the outer node of a multi-targeted project, `KeyComputer.ReferenceAssembly` throws `No reference assembly path`, so every transitive dependent has no key. Reproduced: an app over a `net10.0;netstandard2.0` library reports `0 keys, 1 not computable` on every run. A shared multi-target utility at the bottom of a real repo disables the cache for the whole graph. Fix: when a dependency is an outer node, follow its edge to the inner node for the framework the dependent resolves (`NearestTargetFramework`), or hash every inner node's reference assembly. Check `DependencyCopies` and the `dependency-files` line for the same case, since `OutDir` differs per inner node. The multi-targeted project itself can stay uncached. Done when: an app over a multi-targeted library hits on a clean clone, and the milestone 4 stale checks pass with that library as the edited one.

2. **Done 2026-11-08.** `-c`, `--configuration` and `-p:<name>=<value>` reach restore and the build. One property per `-p`, because splitting on `;` would break values such as `DefineConstants`. A user's `-p` wins over an injected property: that costs hits, never correctness. A Release clean clone is all hits and its tests pass with `--no-build -c Release`.

   The finding: **No `-c` or `-p:` on the CLI.** `BuildOptions.cs` has neither, so CI that builds Release cannot use MIDA. Global properties are already in the key. Done when: `mida build -c Release` hits on a clean clone, Debug and Release keys differ, and `-p:Foo=bar` changes every key.

3. **Decide what to do about version stamping from git.** Reproduced with MinVer 6.0.0 and a `1.0.0` tag. After any commit, a one-line body edit in a leaf library gave 0 hits and 3 misses, because the version reaches `obj/ref` and so every dependent's `dependency-ref` line. A commit that changed no code still hit, but served outputs stamped `1.0.0` where MinVer now says `1.0.1-alpha.0.1`. NBGV and GitVersion behave the same way in principle. `IncludeSourceRevisionInInformationalVersion=false` only covers the SDK's own SHA. Options:
   - Hash reference assemblies with the version attributes masked (`AssemblyVersion`, `AssemblyFileVersion`, `AssemblyInformationalVersion`), through `System.Reflection.Metadata`. Checked 2026-11-08 on .NET 10: an app compiled against `Lib` 2.0.0.0 runs with `Lib` 3.0.0.0, and fails with `FileNotFoundException` on `Lib` 1.0.0.0. So `AssemblyVersion` must stay in the key, since a hit after moving to a branch with a lower version would crash. Masking only `AssemblyFileVersion` and `AssemblyInformationalVersion` is safe, and covers MinVer between major versions, because MinVer sets `AssemblyVersion` to `{Major}.0.0.0`.
   - Or document that these tools defeat early cutoff during Build, and that hits serve stale version stamps, which only matters at pack and publish.

4. **Measure the real repo, and not only on APFS.** The profiling step is done 2026-11-08: 74 misses among 950 hits took 62 s because of two thread-pool starvations inside MSBuild locks, not superlinear work. Saves now run off the plugin hook, and worker nodes start before the graph build, so that run takes 27.6 s ([benchmarks.md](notes/benchmarks.md)). Still to do: run the 49-project repo: hit time with restore, store overhead on a miss, the cost of its five web projects (which always miss now, along with what copies their manifests), and the version-stamping effect if it stamps. Run it on ext4 as well. The 3 s hit relies on APFS cloning (0.9 s), against a plain copy's 18 to 19 s, which is close to the 22 to 28 s compile. If copying dominates there, store files that come from NuGet packages as references to the package folder, like dependency copies, so they are neither stored nor uploaded. Most of the 3.8 GB is package DLLs copied into every `bin/`. Hard links are another option for the local tier. Record the numbers in `docs/notes/local-store.md`, and update the risks.

Smaller, take them when nearby:

- A cache entry can write any file in the checkout, not only its project's outputs: `Validate` in `src/Mida.Store/OutputCache.cs` only checks paths against the repo root. Reproduced with a hand-edited manifest, which wrote an executable `Directory.Build.targets` at the root. Security is not a priority, since the bucket is trusted CI infrastructure. A cheap guard against a buggy writer is to require paths under the project's `BaseOutputPath` and `BaseIntermediateOutputPath`, which `MidaCachePlugin.RemoveObsolete` already computes.
- Done 2026-11-08: `IsCacheFailure` in `MidaCachePlugin.cs` was an allow-list, so a store that threw `NotSupportedException`, `SecurityException` or its own `OperationCanceledException` failed the build with no message. Now every exception is a miss except the build's own cancellation.
- Done 2026-11-08: in `OutputCache.RestoreAsync`, the once-per-run blob check hashed whichever copy was restored first and cached a failure, so one vanished copy made that blob miss for the rest of the run. Each check now reads its own copy, and only a pass is kept.
- Done 2026-11-08: `Validate` accepted a path listed twice, in `files` or `references`, and a path that is a directory of another. Both are now malformed.
- `Directory.Build.rsp` is ignored by the in-process `BuildManager`. Reasoned, not reproduced.

### 5. GCS store

Done 2026-11-08, against [fake-gcs-server](https://github.com/fsouza/fake-gcs-server) 1.56.1 in Docker, which enforces `ifGenerationMatch=0`. It has not met a real bucket, so Application Default Credentials are untested. Without Docker the GCS tests skip.

- `GcsStore` in `Mida.Store` calls the JSON API over `HttpClient`, so the store stays AOT-clean. `Google.Apis.Auth` supplies only the access token, from `src/Mida`, because it pulls Newtonsoft.Json. `STORAGE_EMULATOR_HOST` points it at an emulator, with no credentials, as in Google's own client libraries.
- `TieredStore` reads local first and copies what it fetches from remote into local, so a hit always restores from local disk. It writes remote first and local only when remote took the object. A manifest another writer won therefore comes back from remote and can be compared.
- `--cache` is given once for each tier: `--cache <dir> --cache gs://<bucket>/<prefix>`. `--read-only` stores nothing in either tier. A read-only run that filled the local tier would hide from a later writing run on the same runner that the remote lacks those blobs.
- An upload first asks whether the object exists. Most blobs already do, and a duplicate upload sends the whole body before its 412.
- Each output file is read into memory once, then hashed and stored from those same bytes. That drops cloning on save, which only ever helped the local tier's disk use. A file over 2 GB would fail and be a miss.
- `{NuGetPackageRoot}` was needed. `InitializeSourceRootMappedPaths` returns the package folder as a `SourceRoot` item, so 3 of the 5 test manifests named this machine's NuGet root. The clone test checks that no manifest does.
- A second writer whose manifest differs gets a warning, and the first manifest stays.
- No retries: a 5xx or a timeout is a miss with a warning. No compression: blobs stay byte-identical to outputs. Eviction: one age-based lifecycle rule over the whole prefix removes manifests along with blobs. A manifest whose blob went first then misses until it ages out too. Nothing sets this up yet; it belongs in the milestone 6 README.

Plan as written:

A remote store behind the local one, so every hit still restores by cloning from local disk. Create-only writes with `ifGenerationMatch=0`, 412 treated as success, SHA-256 verified on read, `--read-only`. Auth through Application Default Credentials. Replayed target results may hold package paths, so add a `{NugetPackageRoot}` token if they do.

Hash each blob from the same read that uploads it: hashing and then copying separately lets a file change in between, so a blob can hold bytes that don't match its name. Bucket lifecycle eviction plus create-only manifests means a key whose blob was evicted stays a miss until a byte-identical rebuild, which a nondeterministic output never produces. So eviction must remove manifests along with blobs. Blobs are uncompressed for cloning, so decide on compression and egress cost for the remote tier.

Done when: a hit crosses two machines, or two clones with the local tier disabled, with different NuGet roots; a second writer with a different manifest for the same key is logged, not written.

### 6. MVP release

`mida build [-c <configuration>] [-p:<name>=<value>] [--cache <dir|gs://bucket/prefix>] [--read-only]`, console summary, README install instructions and CI usage (the injected properties, `--no-build` on every later step, caching `~/.nuget/packages` in CI), a clear error when the loaded SDK is not the one `global.json` pins, `dotnet tool install` from a tag.

## Known risks

- Everything so far ran on macOS arm64 and Linux arm64, and Release only on the toy repo on macOS. Windows and Linux x64 are unproven, and the store has not met a repo of real size.
- Nothing evicts `~/.mida/cache`, so it grows without limit.
- The GCS store has only met an emulator. Real buckets, Application Default Credentials, Workload Identity Federation from CI, latency and egress cost are untested. A cold runner fetches each blob in its own request, with no retry.
- The plugin API was experimental until recently and moved namespace between SDK 8 and 10, so a future SDK can break it. The fallback is driving `dotnet build` per project ourselves; keys and the store would not change.
- Real source generators are untested: the build-time dependency rule was checked with a plain library.
- Multi-targeted projects get no key, so they rebuild on every run. Their dependents do get keys. A `netstandard2.0` dependency without `ProduceReferenceAssembly` is keyed by its implementation, so it has no early cutoff.
- Projects with static web assets are never stored: their manifests name the checkout. On SDK 11 that is every Web SDK project, so an ASP.NET-heavy repo gains little until those manifests get a `{RepoRoot}` token.
- Unrelated projects sharing one `OutDir` are unsupported: one can silently restore another's older files. A checkout path containing `'` fails the build with `CS8101`. See [local-store.md](notes/local-store.md).
- Environment variables are not in the key, so one that changes build output gives a false hit.
- Prediction misses undeclared inputs, so the first keys are slightly unsound. Later mitigation: an extra-inputs glob setting and an audit mode that rebuilds a sample of hits and compares outputs.
- Serialized target results tie a cache entry to an MSBuild version. The MSBuild version is in the key, so an SDK bump means a cold cache, not a wrong hit.
- Version stamping (SourceLink, MinVer, GenerateAssemblyInfo) changes outputs per commit. The default commit SHA in `AssemblyInformationalVersion` reaches the reference assembly, so `mida build` turns it off ([relocation.md](notes/relocation.md)). Tools that stamp a version from git (MinVer, NBGV, GitVersion) do the same, and nothing turns them off. That was reproduced with MinVer: early cutoff is lost after any commit, and hits serve stale version stamps. See 4b item 3.
- A missed dependency reference means tests run against stale code and pass. Every change to the manifest or key must keep the milestone 4 stale check.
- A plain `dotnet build` or `dotnet test` after `mida build` recompiles everything (49 of 49 on the test repo), so a CI step that forgets `--no-build` silently loses the saving. If local no-op builds become a goal, the recipe is in [relocation.md](notes/relocation.md).
- **The central performance claim rests on copy-on-write.** A plain copy of outputs takes about as long as compiling them (18.9 s against 27.7 s on the test repo). Typical Linux CI runners use ext4, and Windows NTFS has no reflinks, so a hit there may save little, and a cold runner first downloads about 480 MB of unique blobs. Whether `File.Copy` reflinks on Linux is untested. See 4b item 4.
- Outputs of `mida build` carry `/_/` source paths, which may hurt debugging them locally. Untested. Plain `dotnet build` is unaffected.
- `IsAotCompatible` only checks our own calls. Publish a throwaway AOT console that references `Mida.Store` now and then to catch dependency drift.
