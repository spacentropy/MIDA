# MIDA MVP plan

Status: plan, 2026-11-01, updated 2026-11-17. Milestones 1 to 6 are built, and so is [a week of work past them](#after-milestone-6). The first release waited for selective test execution, decided 2026-11-16, and a first cut of that, [`mida test`](#7-mida-test-selective-test-execution), is built since 2026-11-17. No tag exists yet. **Next: [what is left](#next), in order.** Design: [design.md](design.md). Decisions behind this plan: [cache on object storage](notes/build-cache-object-store.md), [NativeAOT deferred](notes/nativeaot.md), [relocation](notes/relocation.md), [project-cache plugin](notes/project-cache-plugin.md), [cache key](notes/cache-key.md), [local store](notes/local-store.md), [benchmarks and test harness](notes/benchmarks.md), [stored graph](notes/stored-graph.md), [static web assets](notes/static-web-assets.md), [multi-targeting](notes/multi-targeting.md), [`mida test`](notes/mida-test.md).

## Goal

One command, `mida build`, that builds a .NET repo through MSBuild's static graph with a relocatable output cache: local disk first, Google Cloud Storage second. A second `mida build` on a clean clone at a different path is all cache hits, and `dotnet test --no-build` runs on the result. The target is CI build and test time. Local incremental builds already work, so making a plain `dotnet build` a no-op afterwards is not a goal.

Everything else in the design (affected detection, tests, publish, metrics, coverage, CI matrix, last-green refs) stacks on the two primitives this MVP builds: the project graph and the per-project cache key. Affected detection falls out as "cache miss".

## Current state

Milestones 1 to 6 are built, except the first tag. `mida build [solution] [-c <configuration>] [-p:<name>=<value>] [--cache gs://<bucket>[/<prefix>]] [--read-only] [--evaluate]` (`mida build --help` lists every option) first looks in the store for an evaluated graph that fits the checkout. With one, it needs neither restore nor evaluation, and when every project then hits, MSBuild is never loaded ([stored-graph.md](notes/stored-graph.md)). Otherwise it restores, evaluates the static graph, predicts inputs, injects the two properties, and runs an in-process graph build whose plugin computes a key for every project, restores a hit from the local store or through it from GCS, and stores a miss in both. A clean clone at another path is all hits and `dotnet test --no-build` passes on it. See [local-store.md](notes/local-store.md).

The summary ends with the time saved: each manifest stores how long its miss took, and a hit adds that up. Since 2026-11-17 it also says how many graph nodes get no key and why, such as `3 nodes without a key: 3 multi-targeted`, because those are built on every run and are in neither the hits nor the misses. `mida build` stops with an error when it cannot load the SDK `global.json` selects. It ships as the `mida` tool package, which a `v*` tag would push to nuget.org. No tag exists, so today it is packed from source.

`mida test` takes the same options, builds the same way, and then runs each test project whose pass is not in the store, several at once, without MSBuild. A pass is stored under a key of the sources, packages and properties of the test project and of everything it references, plus the runner arguments after `--`. `--all` runs everything. See [mida-test.md](notes/mida-test.md).

Diagnostics today: `--cache <dir>` to use a local store other than `~/.mida/cache`, `--evaluate` to ignore stored graphs, `--print-keys`, `--key-details <dir>` to write each key's lines, and `MIDA_BINLOG=<path>` for a binlog. Tests: `dotnet test --project tests/Mida.Tests -f net10.0` (and `-f net11.0`; without `-f` the command fails on a path bug), 210 on net10.0 and 211 on net11.0 on 2026-11-16 and six more since, which no full run has counted yet, about 13 minutes each on a hosted runner, including real builds and real test runs of a small repo and of a generated 8-project graph. The GCS tests use the bucket `MIDA_TEST_BUCKET` names, else fake-gcs-server through Testcontainers, and skip with neither. Benchmarks on a generated repo and builds of two real repos are `[Explicit]` tests, see [benchmarks.md](notes/benchmarks.md). After a failed build, `--no-build` runs stale binaries, so check the build first.

Where it stands, 2026-11-19, as `mida build` over a plain `dotnet build` on Linux x64 and ext4, where a hit is a plain copy:

| repo | where | plain build | empty cache | filled cache, clean clone at another path |
|---|---|---|---|---|
| a web CMS, 227 projects | GitHub hosted runner, 4 processors | 162.9 s | 1.09 | 0.10, on 448 hits |
| an application framework, 270 projects | the same | 123.2 s | 1.38 | 0.82, on 163 hits. Its 107 multi-targeted projects have no key and are built |
| generated, 1024 projects | Cloud Build private pool `mida-8`, 8 processors, the machine mida is tuned for | 112 s | 0.93 | 0.51 unchanged, 0.57 after a body edit, 0.61 after an API edit |

Both real repos leave the same files under `bin` as `dotnet build`. The generated projects are one class each, so that row is the cache's overhead and not its saving.

## Scope

In:

- SDK-style C# projects, one target framework per project, `ProjectReference` graphs, Central Package Management.
- `dotnet build` semantics on Linux, macOS and Windows.
- .NET SDK 10 and 11 only, the supported versions. Projects may target any framework those SDKs can build, since real repos still target `net8.0` and `netstandard2.0`.
- The newest tools only, decided 2026-11-17: a case that only an older tool brings is not handled. Tests run on Microsoft.Testing.Platform only, no VSTest, with the current major of TUnit, xUnit, NUnit or MSTest. No F# or Visual Basic.
- Local disk store, then GCS. Read-only mode for PR runners. MIDA is a CI tool: laptops run it only to develop MIDA itself, so laptop hits on CI entries are not a goal. Platform and the exact SDK patch are in the key anyway, so a Mac would never hit Linux CI entries.
- Console summary: hits, misses, time saved.

After the MVP came test running and caching, the other half of CI time, and the gate for the first release. See [milestone 7](#7-mida-test-selective-test-execution).

Later: selective NuGet caching from the bucket, fetching only the packages that projects missing the cache need. A cold restore takes 15.7 s on the test repo against 1.8 s with packages present, the largest part of a hit on a fresh runner. Until then CI caches `~/.nuget/packages` itself. Since 2026-11-12 a stored graph brings `project.assets.json` back, so resolved versions no longer need a restore. The packages themselves still do: a restore is skipped only when they are all on disk.

Out, named so nobody builds them by accident: publish, OpenTelemetry, coverage, last-green refs, CI generation, S3 and Azure backends, NativeAOT, any daemon or server. Caching multi-targeted projects themselves was on this list until 2026-11-13 and is now planned, see [multi-targeting.md](notes/multi-targeting.md).

## Decisions already made

| Decision | Choice | Why |
|---|---|---|
| Packaging | Framework-dependent `dotnet tool` multi-targeting `net10.0;net11.0`; MSBuild loaded from the installed SDK through MSBuildLocator. A host cannot load a newer SDK's MSBuild, so a CI image with only SDK 10 needs the `net10.0` build | Exact fidelity with `dotnet build`; NuGet-delivered and workload SDKs work. AOT is deferred, see [nativeaot.md](notes/nativeaot.md); the rules that keep it possible are in design §1.6. |
| MSBuild integration | MSBuild project-cache plugin, registered programmatically through `BuildParameters.ProjectCacheDescriptor` on an in-process `BuildManager` graph build | Cache get has worked since MSBuild 16.9 on every platform. The project-finished hook fires without `/reportfileaccesses`, so cache add works under `dotnet build` everywhere ([spec](https://github.com/dotnet/msbuild/blob/main/documentation/specs/project-cache.md), "Cache add scenario"). No project files are touched. The API is `Microsoft.Build.ProjectCache`, identical in SDK 10 and 11. |
| Cache key | Single-phase hash of: predicted input file contents ([Microsoft.Build.Prediction](https://github.com/microsoft/MSBuildPrediction)), repo-local imported props and targets, resolved packages from `project.assets.json`, transitive dependency reference assemblies (`obj/ref`), the full key of build-time dependencies such as source generators, global properties minus an ignore list, SDK version, MSBuild version, platform. Computed during the build, in topological order. See [cache-key.md](notes/cache-key.md) | Without Detours, BuildXL's weak and strong fingerprints collapse into one. Prediction is MIT and maintained (1.2.44, October 2026). |
| Outputs | Predicted output directories scanned after the build, minus the exclusions in [relocation.md](notes/relocation.md). File contents are not rewritten and timestamps are left alone, except in the five static web asset manifests, which are stored with tokens for the checkout's roots since 2026-11-12 ([static-web-assets.md](notes/static-web-assets.md)). Manifest paths stay relative to the repo root, and replayed target results carry `{RepoRoot}` and `{NuGetPackageRoot}`. Files in `bin/` that came from a project dependency are stored as references and resolved to its current file on every hit, because the ref-based key does not cover them ([why](notes/project-cache-plugin.md#stale-dependency-copies)). Target results serialized as JSON through source generators and replayed on a hit | Borrowed from [MSBuildCache](https://github.com/microsoft/MSBuildCache) (MIT). Replayed results are the spec's "most correct" hit option. |
| Injected properties | `mida build` always passes `ContinuousIntegrationBuild=true` and `IncludeSourceRevisionInInformationalVersion=false` as global properties on every run. Nothing is added to the repo | Portable PDB paths and byte-identical outputs; a commit SHA in the reference assembly would defeat early cutoff. See [relocation.md](notes/relocation.md). |
| Restore | Until 2026-11-12 not cached: `mida build` ran restore first, then the cached graph build. Now skipped when a stored graph fits the checkout and every package is on disk. `project.assets.json` and the generated props and targets then come from the store, with tokens in place of its absolute paths. Packages are not cached | Restore was half of an all-hit build, and most of it was a second evaluation. See [stored-graph.md](notes/stored-graph.md). |
| Store | `IObjectStore` from [the note](notes/build-cache-object-store.md), cut to three methods, where a restore takes a path so a filesystem store can clone: content-addressed blobs plus manifests, create-only writes, SHA-256 verified on read, once per blob per run. A local filesystem store, with GCS behind it since milestone 5 | No server. No BuildXL cache libraries: they pull RocksDB native binaries and are not trim-clean. |
| Command line | System.CommandLine 2.0.14 | Help, errors and response files for free. Its `net8.0` assembly is marked trimmable, and on 2026-11-08 a NativeAOT publish of the real option code gave no trim or AOT warnings and parsed correctly as a native binary. Stricter than `dotnet build`: a repeated `-c` is an error, and a parse error exits 1. |
| Failures | A key that cannot be computed, or any store failure, is a miss with a warning. It never fails the build | The cache must not become a new way to break CI. An exception that escapes a plugin hook does fail the build, so the plugin catches them. |
| Not used | BuildXL as executor, BuildXL cache libraries, MSBuildCache's Detours-based add path | Executor constraint, dependency weight, Windows-only. |

## Layout

```
src/Mida/          CLI, MSBuild hosting (Locator), project-cache plugin, key computation, stored graph, test runner
src/Mida.Store/    IObjectStore, manifest model, restore and save, local filesystem store, GCS store. No MSBuild dependency, AOT-clean
tests/Mida.Tests/  TUnit on Microsoft.Testing.Platform (set in global.json)
MIDA.slnx          All three projects
.github/workflows/ ci.yml: tests on Linux, net10.0 and net11.0, on a hosted runner. release.yml: pack and push to nuget.org on a v* tag.
                   bench.yml: the benchmark scenarios on the packed tool, on a push to main, on the pool
                   realworld.yml: builds of three real repos and the tests of two, when dispatched, on the pool since 2026-11-19
                   gcs.yml: the GCS tests against a real bucket, on a push to main, on the pool
.github/dependabot.yml  Weekly updates of packages, the SDK and the pinned actions, each release a week old. Not the MSBuild packages, whose pins move by hand
.github/actions/pool, scripts/pool.sh, cloudbuild.yaml
                   Run one command on a Cloud Build private pool and copy its TestResults back
scripts/pool-setup.sh, scripts/pool-github.sh
                   Provision the pool, its buckets and keyless sign-in from GitHub Actions. Project ids live in repo secrets
```

Conventions already in place: `Directory.Build.props` (warnings as errors, all analyzers, `IsAotCompatible`, `net10.0;net11.0`, `runtime-async` on `net11.0` only), SDK pinned in `global.json`. Tests use TUnit only, including test projects that fixtures generate; the test project is the one place `IsAotCompatible` is off, because it never ships. Only `src/Mida` may reference packages that are not trim-clean (Locator, Prediction, Google.Apis.Auth). A new package that claims to be trim-clean is checked with a throwaway NativeAOT publish, as System.CommandLine was. Keep MSBuild types out of `Mida.Store`. MSBuild packages compile against 18.9.6 on `net10.0`, the oldest supported API and the last with a `net10.0` build, and against 18.12.2 on `net11.0`, where Prediction needs 18.11 or newer, with `ExcludeAssets="runtime" PrivateAssets="all"` so the SDK's copies load at run time; Prediction brings `Microsoft.NET.StringTools` in, so that one needs the same treatment. Code that touches Microsoft.Build types must sit in a method that runs after `MSBuildLocator.RegisterDefaults()`, otherwise the JIT binds the types before the SDK path is registered.

## Milestones

Each ends with a check that can be run. Do them in order. Milestones 1 and 2 decide details of 3 to 5. Milestones 1 to 6 are history, kept for the reasons in them. What is open is under [Next](#next).

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
- A project with static web assets is not stored, because its manifests name the checkout. On SDK 11 that is every Web SDK project. The 49-project repo has five, so measure what that costs when it runs, and relocate these manifests if the cost is real. Done 2026-11-12, after an application framework of 270 projects lost 41 of 163 hits to it: [static-web-assets.md](notes/static-web-assets.md).

The test suite also moved from xUnit v3 to TUnit, and the stale-copy mutation checks were repeated on it.

`IObjectStore` plus the filesystem implementation under `~/.mida/cache`, manifest format including dependency references, upload after a miss, materialize on a hit using copy-on-write where the filesystem has it. Blobs are per file: outputs on the test repo dedupe 7.9x ([relocation.md](notes/relocation.md)). Replay target results for every requested target, with `{RepoRoot}` restored. Normalize predicted output directories, which came back with mixed separators on macOS (`obj\Debug/net8.0/`).

Done when: `mida build` on a clean clone at a different path reports all hits, and `dotnet test --no-build` passes on the result. After a method body edit in a library, a test covering that code fails, both when the library misses and when it hits from a newer entry than its dependents. The same holds for a file the library copies to output, such as `appsettings.json`. A manifest whose blob is missing is a miss, not an error. All of it also passes on Linux.

### 4b. Fixes from the adversarial review

Added 2026-11-07. Items 1 to 4 and three of the smaller items are done. The two smaller items left are under [Next](#next). Two reviews of the finished milestone 4 by a second model. They could not break relocation, key coverage or stale-copy handling: inputs such as `.editorconfig`, `global.json`, `Directory.Build.targets` and out-of-tree `Compile` items are in the key, manifests are byte-identical across checkouts, and `UseArtifactsOutput` hits with current dependency copies. What they did find is below, in order. Items 1 and 2 are code. Item 3 is a decision. Item 4 is the measurement milestone 4 skipped.

1. **Done 2026-11-08.** A dependency that is an outer node now adds the lines of every inner node, so its dependents get keys. Resolving the one framework the dependent picks was not needed. `DependencyCopies` already worked, because the closure reaches the inner nodes. The end-to-end tests run the clean clone and both stale checks a second time with `LibB` on `net10.0;netstandard2.0`. A `netstandard2.0` project has no reference assembly unless `ProduceReferenceAssembly` is set, so the key hashes its implementation, and any body edit misses every dependent. That holds for single-target `netstandard2.0` projects too, and outside git that implementation names the checkout, so the test sets the property.

   The finding: **One multi-targeted library turns the cache off above it.** For a dependency that is the outer node of a multi-targeted project, `KeyComputer.ReferenceAssembly` throws `No reference assembly path`, so every transitive dependent has no key. Reproduced: an app over a `net10.0;netstandard2.0` library reports `0 keys, 1 not computable` on every run. A shared multi-target utility at the bottom of a real repo disables the cache for the whole graph. Fix: when a dependency is an outer node, follow its edge to the inner node for the framework the dependent resolves (`NearestTargetFramework`), or hash every inner node's reference assembly. Check `DependencyCopies` and the `dependency-files` line for the same case, since `OutDir` differs per inner node. The multi-targeted project itself can stay uncached. Done when: an app over a multi-targeted library hits on a clean clone, and the milestone 4 stale checks pass with that library as the edited one.

2. **Done 2026-11-08.** `-c`, `--configuration` and `-p:<name>=<value>` reach restore and the build. One property per `-p`, because splitting on `;` would break values such as `DefineConstants`. A user's `-p` wins over an injected property: that costs hits, never correctness. A Release clean clone is all hits and its tests pass with `--no-build -c Release`.

   The finding: **No `-c` or `-p:` on the CLI.** `BuildOptions.cs` has neither, so CI that builds Release cannot use MIDA. Global properties are already in the key. Done when: `mida build -c Release` hits on a clean clone, Debug and Release keys differ, and `-p:Foo=bar` changes every key.

3. **Done 2026-11-08, the first option.** `dependency-ref` hashes a reference assembly's metadata tables and heaps with blob columns by content, so a version string that changes length moves nothing else. The GUID heap, which holds only the MVID, is left out, and so are the two masked attribute values and everything outside the metadata, such as the Win32 version resource. A row-size check against `System.Reflection.Metadata` guards the table schema, and passed on all 10,611 assemblies of the installed SDKs; on a failure the key falls back to the whole file. An implementation hashed for want of a reference assembly is still hashed whole. The end-to-end test stamps versions from a file in a target, the way tools that stamp a version from git do: a body edit under a new patch version hits every dependent, and one under a new major version misses them. A stamped project's own outputs can still carry an older version after a hit, which the README states.

   The finding: **Decide what to do about version stamping from git.** Reproduced with a tool that stamps a version from git and a `1.0.0` tag. After any commit, a one-line body edit in a leaf library gave 0 hits and 3 misses, because the version reaches `obj/ref` and so every dependent's `dependency-ref` line. A commit that changed no code still hit, but served outputs stamped `1.0.0` where the tool now says `1.0.1-alpha.0.1`. Other such tools behave the same way in principle. `IncludeSourceRevisionInInformationalVersion=false` only covers the SDK's own SHA. Options:
   - Hash reference assemblies with the version attributes masked (`AssemblyVersion`, `AssemblyFileVersion`, `AssemblyInformationalVersion`), through `System.Reflection.Metadata`. Checked 2026-11-08 on .NET 10: an app compiled against `Lib` 2.0.0.0 runs with `Lib` 3.0.0.0, and fails with `FileNotFoundException` on `Lib` 1.0.0.0. So `AssemblyVersion` must stay in the key, since a hit after moving to a branch with a lower version would crash. Masking only `AssemblyFileVersion` and `AssemblyInformationalVersion` is safe, and covers such a tool until `AssemblyVersion` changes, which, depending on the tool, is on a major, minor or patch version by default.
   - Or document that these tools defeat early cutoff during Build, and that hits serve stale version stamps, which only matters at pack and publish.

4. **Done 2026-11-12, on other repos.** A web CMS of 227 projects and an application framework of 270 stand in for the 49-project repo: they are public, larger, and run in CI. Both ran on ext4, in the OrbStack container and on GitHub's hosted runners, so a hit there is a plain copy. A filled cache is 0.10 of a plain build on the web CMS and 0.82 on the framework ([benchmarks.md](notes/benchmarks.md#real-world-repos)). The web projects' cost was real, 41 of 163 hits on the framework and 109 of 448 on the web CMS, and is fixed ([static-web-assets.md](notes/static-web-assets.md)). Not done: timings of the 49-project repo, the version-stamping effect on a real repo, and package files as references, which is now under [Next](#next) with its evidence.

   The finding: **Measure the real repo, and not only on APFS.** The profiling step is done 2026-11-08: 74 misses among 950 hits took 62 s because of two thread-pool starvations inside MSBuild locks, not superlinear work. Saves now run off the plugin hook, and worker nodes start before the graph build, so that run takes 27.6 s ([benchmarks.md](notes/benchmarks.md)). Still to do: run the 49-project repo: hit time with restore, store overhead on a miss, the cost of its five web projects (which always miss now, along with what copies their manifests), and the version-stamping effect if it stamps. Run it on ext4 as well. The 3 s hit relies on APFS cloning (0.9 s), against a plain copy's 18 to 19 s, which is close to the 22 to 28 s compile. If copying dominates there, store files that come from NuGet packages as references to the package folder, like dependency copies, so they are neither stored nor uploaded. Most of the 3.8 GB is package DLLs copied into every `bin/`. Hard links are another option for the local tier. Record the numbers in `docs/notes/local-store.md`, and update the risks.

Smaller, take them when nearby:

- A cache entry can write any file in the checkout, not only its project's outputs: `Validate` in `src/Mida.Store/OutputCache.cs` only checks paths against the repo root. Reproduced with a hand-edited manifest, which wrote an executable `Directory.Build.targets` at the root. Security is not a priority, since the bucket is trusted CI infrastructure. A cheap guard against a buggy writer is to require paths under the project's `BaseOutputPath` and `BaseIntermediateOutputPath`, which `MidaCachePlugin.RemoveObsolete` already computes.
- Done 2026-11-08: `IsCacheFailure` in `MidaCachePlugin.cs` was an allow-list, so a store that threw `NotSupportedException`, `SecurityException` or its own `OperationCanceledException` failed the build with no message. Now every exception is a miss except the build's own cancellation.
- Done 2026-11-08: in `OutputCache.RestoreAsync`, the once-per-run blob check hashed whichever copy was restored first and cached a failure, so one vanished copy made that blob miss for the rest of the run. Each check now reads its own copy, and only a pass is kept.
- Done 2026-11-08: `Validate` accepted a path listed twice, in `files` or `references`, and a path that is a directory of another. Both are now malformed.
- `Directory.Build.rsp` is ignored by the in-process `BuildManager`. Reasoned, not reproduced.

### 5. GCS store

Done 2026-11-08, against [fake-gcs-server](https://github.com/fsouza/fake-gcs-server) 1.56.1 in Docker, which enforces `ifGenerationMatch=0`. Since 2026-11-15 the same tests take a real bucket from `MIDA_TEST_BUCKET`, with Application Default Credentials, and `scripts/pool.sh` runs the suite that way on the pool. Since 2026-11-17 `gcs.yml` runs `GcsStoreTests` and the two end-to-end bucket tests that way on every push to `main`, and they pass against the real bucket with none skipped. Its one red run, 2026-11-19, was a 429 from streaming the build log, which polls since. With neither a bucket nor Docker the GCS tests skip.

- `GcsStore` in `Mida.Store` calls the JSON API over `HttpClient`, so the store stays AOT-clean. `Google.Apis.Auth` supplies only the access token, from `src/Mida`, because it pulls Newtonsoft.Json. `STORAGE_EMULATOR_HOST` points it at an emulator, with no credentials, as in Google's own client libraries.
- `TieredStore` reads local first and copies what it fetches from remote into local, so a hit always restores from local disk. It writes remote first and local only when remote took the object. A manifest another writer won therefore comes back from remote and can be compared.
- `--cache` is given once for each tier: `--cache <dir> --cache gs://<bucket>/<prefix>`. `--read-only` stores nothing in either tier. A read-only run that filled the local tier would hide from a later writing run on the same runner that the remote lacks those blobs.
- An upload first asks whether the object exists. Most blobs already do, and a duplicate upload sends the whole body before its 412.
- Each output file is read into memory once, then hashed and stored from those same bytes. That drops cloning on save, which only ever helped the local tier's disk use. A download from GCS is also held whole in memory before it lands in the local tier. Either way, a file over 2 GB would fail and be a miss.
- `{NuGetPackageRoot}` was needed. `InitializeSourceRootMappedPaths` returns the package folder as a `SourceRoot` item, so 3 of the 5 test manifests named this machine's NuGet root. The clone test checks that no manifest does.
- A second writer whose manifest differs gets a warning, and the first manifest stays.
- No retries: a 5xx or a timeout is a miss with a warning. No compression: blobs stay byte-identical to outputs. Eviction: one age-based lifecycle rule over the whole prefix removes manifests along with blobs. A manifest whose blob went first then misses until it ages out too. Nothing in mida sets this up; the README gives the rule.

Plan as written:

A remote store behind the local one, so every hit still restores by cloning from local disk. Create-only writes with `ifGenerationMatch=0`, 412 treated as success, SHA-256 verified on read, `--read-only`. Auth through Application Default Credentials. Replayed target results may hold package paths, so add a `{NugetPackageRoot}` token if they do.

Hash each blob from the same read that uploads it: hashing and then copying separately lets a file change in between, so a blob can hold bytes that don't match its name. Bucket lifecycle eviction plus create-only manifests means a key whose blob was evicted stays a miss until a byte-identical rebuild, which a nondeterministic output never produces. So eviction must remove manifests along with blobs. Blobs are uncompressed for cloning, so decide on compression and egress cost for the remote tier.

Done when: a hit crosses two machines, or two clones with the local tier disabled, with different NuGet roots; a second writer with a different manifest for the same key is logged, not written.

### 6. MVP release

Code done 2026-11-08. The first tag is put off until [milestone 7](#7-mida-test-selective-test-execution) is built, decided 2026-11-16: the first release should select tests, not only cache builds. `release.yml` is ready for trusted publishing, with the `release` environment and the `NUGET_USER` variable, and no API key is stored. The policy on nuget.org is left out on purpose until then.

- Time saved: the manifest has a `buildTime`, from the plugin's miss answer to the project-finished hook, and a hit adds it up. It is summed over projects, so not wall-clock time. A second writer's manifest is compared with the build time left out, since every writer times differently. A manifest without the field reads as zero.
- The SDK check is real, not hypothetical. MSBuildLocator skips an SDK newer than its runtime, so the `net10.0` build in a repo that pins SDK 11 loaded 10.0.403 without a word. `mida build` now runs `dotnet --version` in the solution's directory, from the dotnet the SDK came from, and stops when the two differ. The `net10.0` test run had that mismatch: fixtures built with SDK 10 and ran `dotnet test` with SDK 11. Fixtures now pin the newest SDK of the runtime under test, and the runner clears the `MSBuildExtensionsPath` and `MSBuildSDKsPath` that `dotnet test` sets, which mixed SDK 11 targets into a fixture's SDK 10 `dotnet pack`.
- Found on the way: `mida build` without a solution always failed. A System.CommandLine validator's `GetValue` skips `DefaultValueFactory`, and every test passed the solution. The end-to-end tests now leave it out.
- Packaging: `PackAsTool` with `tools/net10.0` and `tools/net11.0`, the README in the package, no MSBuild assemblies. Installed from a local feed with `dotnet tool install` and checked to build and hit. `.github/workflows/release.yml` packs on a `v*` tag, with the tag as the version, and pushes to nuget.org, where the `mida` id was free on 2026-11-08.

Plan as written:

`mida build [-c <configuration>] [-p:<name>=<value>] [--cache <dir>] [--cache gs://<bucket>[/<prefix>]] [--read-only]`, console summary, README install instructions and CI usage (the injected properties, `--no-build` on every later step, caching `~/.nuget/packages` in CI, `--read-only` on PR runners, credentials through ADC or Workload Identity Federation, and the bucket's age-based lifecycle rule from milestone 5), one run against a real bucket, a clear error when the loaded SDK is not the one `global.json` pins, `dotnet tool install` from a tag.

### After milestone 6

Built 2026-11-09 to 2026-11-16, none of it in the plan above:

- **A wrong hit fixed**, 2026-11-09: a file a dependency's target generated into `bin/` replaced the project's own copy of that name. `TieredStore` also downloads each blob once ([local-store.md](notes/local-store.md)).
- **Benchmarks in CI**, 2026-11-10: `Fresh`, `Unchanged`, `BodyEdit` and `ApiEdit` at 1024 generated projects, the packed tool beside a plain `dotnet build`, the ratio beside the last run's. Each fails unless the run hits and misses exactly the projects the graph says ([benchmarks.md](notes/benchmarks.md)).
- **Where an all-hit build spends its time**, 2026-11-11: restore with NuGet's static graph evaluation, no background GC, and worker nodes that start during evaluation.
- **The stored graph**, 2026-11-12: no restore and no evaluation when the project files did not change, and no MSBuild when every project hits ([stored-graph.md](notes/stored-graph.md)).
- **Real repos**, 2026-11-11: a web CMS and an application framework at pinned tags, each built four times and compared file by file with a plain build. They found the next two.
- **One framework in `TargetFrameworks`** is cached, 2026-11-12: the web CMS writes all 227 of its projects that way and got no keys ([cache-key.md](notes/cache-key.md)).
- **Static web assets** are cached, 2026-11-12: 41 of the framework's 163 hits and 109 of the web CMS's 448 were lost to them ([static-web-assets.md](notes/static-web-assets.md)).
- **A plan for multi-targeted projects**, 2026-11-13, after two reviews. Not built ([multi-targeting.md](notes/multi-targeting.md)).
- **A Cloud Build private pool**, 2026-11-15 and 16: the benchmark runs there, on one machine type, since the ratio moved by 18 to 23% between hosted runners. The GCS tests can take a real bucket. Releases use nuget.org trusted publishing, and every action is pinned to a commit.
- **A lost package fixed, and the unkeyed reported**, 2026-11-17: a project with one framework in `TargetFrameworks` and `GeneratePackageOnBuild` hit without writing its `.nupkg`, reproduced as `6 hits, 0 misses` and no package. The review that day found the same with a single `TargetFramework`. No node of a project that packs on build has a key now, and the summary counts the nodes without a key by reason. Steps 1 and 2 of [multi-targeting.md](notes/multi-targeting.md#plan).

### 7. `mida test`: selective test execution

Built 2026-11-17 as a first cut, see [mida-test.md](notes/mida-test.md), which has the spike's findings. It was the gate for the first release. Choosing test projects, building only what they need, and a project's opt-in and opt-out of stored passes followed 2026-11-20. Not done: the release itself, under [Next](#next).

Changes from the plan below:

- mida starts each test project itself with `dotnet exec`, so testing needs no MSBuild and no `dotnet test`. Microsoft.Testing.Platform only: a VSTest project counts as failed (see [Scope](#scope)). `--project` takes one project, and each `dotnet test` evaluates its project again.
- The stored graph holds the marker: `IsTestingPlatformApplication`, else `IsTestProject`.
- A node's part of the test key is one line with the hash of its own key lines, because a key sorts its lines.
- As many test projects at once as there are processors, with only a failure's output shown. `--all` is the way to run everything.
- A test project that cannot be started, or whose runner refuses its arguments, counts as failed.
- The benchmark has no body edit scenario: its tests fail, as they must, and a failed run is not timed.

First numbers, 2026-11-17, 128 generated projects with 28 test projects, macOS arm64, `mida test` over `dotnet test`: 1.04 with an empty cache, 0.07 on an unchanged clean clone, 0.53 after an API edit of a leaf that reaches 3 test projects ([benchmarks.md](notes/benchmarks.md#mida-test-2026-11-17)). Not yet measured on Linux or at 1024 projects.

Plan as written:

`mida test` builds through the cache as `mida build` does, then runs only the test projects whose result is not in the store.

- **Test key.** A hash of the lines a project's key gets from the project itself (`input`, `missing`, `packages`, `property`, `sdk`, `msbuild`, `platform`) for the test project and every project in its transitive closure, plus the arguments passed to the runner. No reference assemblies: a body edit two levels down must run the test. No outputs either, so the key needs no build, and a multi-targeted dependency without a build key still has these lines.
- **Result.** A passed run stores a small record under `v1/test/<key>`, create-only, with its duration. A failed run stores nothing, so it runs again. `--read-only` stores nothing.
- **Summary.** Test projects run, passed from the store, failed, and the test time saved. The exit code is the runner's.
- **Spike first, half a day.** What marks a test project (`IsTestProject`, which the stored graph does not hold yet). How to run several with `--no-build` on SDK 10 and 11, for Microsoft.Testing.Platform and for VSTest: one `dotnet test` per project, one for all, or the test executables themselves. What the runner's exit codes mean when no test matches a filter.
- **Known unsound.** A test that reads the clock, the network, a database or an environment variable passes from the store when the world changed. A way to run everything is needed from the start. Whether a project can opt out is open.

Done when, on the generated repo, where the graph is the oracle: an unchanged clean clone runs no test project; after a body edit `mida test` runs exactly the test projects in `Dependents(project)`, and exactly the ones the oracle names fail; after an API edit it runs the same set and none fail; a failed project runs again on the next run; a read-only run stores no result. Then a benchmark scenario beside `dotnet test`, and the release: the trusted publishing policy on nuget.org, a `v0.1.0` tag, and the README's install line made true.

Later, not in this milestone: build only what the tests to run need, shard, and test-level selection.

## Next

In order. The first two are small and come before the milestone.

1. **Done 2026-11-17.** A package lost on a hit: see [after milestone 6](#after-milestone-6).
2. **A real bucket in CI.** `gcs.yml` has run `GcsStoreTests` and the two end-to-end bucket tests against the pool's test bucket on every push to `main` since 2026-11-17, all passing, none skipped. Since 2026-11-20 `bench.yml` and `realworld.yml` time every filled cache through that bucket, from an empty local cache ([benchmarks.md](notes/benchmarks.md#in-ci)). The first `bench.yml` run through it: a filled cache costs 0.04 to 0.06 of a plain build more than a local one, an empty cache nothing. Left: a `realworld.yml` run through it. Workload Identity Federation from a hosted runner into `mida build` itself stays untested until a repo uses it.
3. **What is left of [milestone 7](#7-mida-test-selective-test-execution), then the first release.** `mida test` is built, and since 2026-11-20 chooses test projects with `--include`, `--exclude` and `--category`, builds only what the chosen need, and takes `MidaStoreTestPasses` to opt a project in or out of stored passes ([mida-test.md](notes/mida-test.md#what-it-does)). The four `Test*` benchmark scenarios and the real-world test that runs the web CMS's tests have run on the pool since 2026-11-19. Left: the release, the trusted publishing policy on nuget.org, a `v0.1.0` tag, and the README's install line made true.
4. **Multi-targeted projects**, steps 3 to 9 of [multi-targeting.md](notes/multi-targeting.md#plan). The application framework builds 107 of its 270 projects five times each on every run, which is the difference between its 0.82 and the web CMS's 0.10.
5. **What a hit writes.** On the pool an all-hit run of the 1024-project repo keeps the processors 7% busy with 71% iowait: it waits on the disk. At 128 projects it writes 1.56 GB to restore 46 MB of blobs, mostly package files copied into every test project's `bin/`. Store files that come from a package as references to the package folder, like dependency copies, or hard-link from the local store. A write through a hard link would change the blob, so that one needs a guard.
6. **What a miss costs.** A fresh `mida build` is 1.09 to 1.38 of a plain build, and used about 30% more CPU time on the generated repo. Not profiled.
7. **Files written outside the output directories.** Since 2026-11-18 a project that copies such a file to its output gets no key, a stopgap for a source file one project writes from a template into `obj/`: capture `obj/<configuration>/` instead, see [relocation.md](notes/relocation.md). Since 2026-11-21 a hit makes the empty directories under `bin/` and `obj/` too, which fixed 52 of the web CMS's 2,298 tests that failed after an all-hit run ([local-store.md](notes/local-store.md#what-is-stored)), and a build that writes into its project's directory is stored only when `MidaSourceOutputs` names what it writes ([relocation.md](notes/relocation.md#what-goes-in-the-manifest)). Left: whether the two reads of a project's directory per miss cost time on the pool, which no benchmark has run since; a `realworld.yml` run to see which projects of the application framework, a cloud app stack and an SDK repo with 80 test projects the check refuses; and a second run in the same checkout of the web CMS, which evaluates the graph again because a hit changed a directory the stored graph listed.
8. Smaller, when nearby: keep a manifest's paths under its project's `bin/` and `obj/` (4b), `Directory.Build.rsp` (4b), eviction of `~/.mida/cache`, a retry in `GcsStore`, a first run on Windows, the 2026-11-16 pool numbers as the benchmark's new baseline once a second run confirms them.

## Known risks

- It has run on macOS arm64, Linux arm64 and, since 2026-11-10, Linux x64 on hosted runners and the pool, on two real repos of 227 and 270 projects. Windows has never been run.
- Nothing evicts `~/.mida/cache`, so it grows without limit.
- The GCS store meets an emulator in `ci.yml` and the pool's real bucket in `gcs.yml`, `bench.yml` and `realworld.yml`, with the metadata server's credentials. Workload Identity Federation from CI and egress cost are untested: the bucket is in the pool's region. A cold runner fetches each blob in its own request, with no retry, so one 5xx is a miss, and a benchmark that expects exact hits then fails.
- A stored graph stands for an evaluation only while the recorded questions cover everything the evaluation read. A read no question records gives a stale graph, so wrong keys. What is known to escape is refused at store time ([stored-graph.md](notes/stored-graph.md#when-it-is-not-used)).
- The plugin API was experimental until recently and moved namespace between SDK 8 and 10, so a future SDK can break it. The fallback is driving `dotnet build` per project ourselves; keys and the store would not change.
- Real source generators are untested: the build-time dependency rule was checked with a plain library.
- Multi-targeted projects get no key, so they rebuild on every run. The hits and misses count only keyed nodes, and since 2026-11-17 a line of the summary counts the rest. On the application framework that is 107 of 270 projects. Their dependents do get keys, and since 2026-11-12 so does a project with one framework in `TargetFrameworks` ([cache-key.md](notes/cache-key.md)). Any project that sets `GeneratePackageOnBuild` is built on every run, whether or not `IsPackable` is false. A dependent can take a file from the wrong framework's output ([multi-targeting.md](notes/multi-targeting.md#found-by-the-reviews)). A `netstandard2.0` dependency without `ProduceReferenceAssembly` is keyed by its implementation, so it has no early cutoff.
- Static web asset manifests are stored with tokens for the checkout's roots since 2026-11-12 ([static-web-assets.md](notes/static-web-assets.md)). A project with an asset outside those roots is still never stored, and Windows has not been run.
- Unrelated projects sharing one `OutDir` are unsupported: one can silently restore another's older files. A checkout path containing `'` fails the build with `CS8101`. See [local-store.md](notes/local-store.md).
- Environment variables are not in the key, so one that changes build output gives a false hit. A stored graph does record the variables an evaluation read, and evaluates again when one changes, but the key that follows has no line for them.
- Prediction misses undeclared inputs, so the first keys are slightly unsound. Later mitigation: an extra-inputs glob setting and an audit mode that rebuilds a sample of hits and compares outputs.
- Serialized target results tie a cache entry to an MSBuild version. The MSBuild version is in the key, so an SDK bump means a cold cache, not a wrong hit.
- Version stamping (SourceLink, GenerateAssemblyInfo, version tools) changes outputs per commit. The default commit SHA in `AssemblyInformationalVersion` reaches the reference assembly, so `mida build` turns it off ([relocation.md](notes/relocation.md)). Tools that stamp a version from git do the same, and nothing turns them off. Since 4b item 3 the two version attributes they change on every commit are left out of the reference assembly's hash, so early cutoff is lost only when `AssemblyVersion` changes. Hits still serve stale version stamps, and no real repo that stamps has been run.
- `mida test` does not run a test again when only something outside its key changed: the clock, the network, a database, an environment variable, a file that is no build input, or the runtime's patch version. `--all` runs everything, and `MidaStoreTestPasses` false runs one project every time ([mida-test.md](notes/mida-test.md#known-unsound)).
- A missed dependency reference means tests run against stale code and pass. Every change to the manifest or key must keep the milestone 4 stale check.
- A plain `dotnet build` or `dotnet test` after `mida build` recompiles everything (49 of 49 on the test repo), so a CI step that forgets `--no-build` silently loses the saving. If local no-op builds become a goal, the recipe is in [relocation.md](notes/relocation.md).
- **Without copy-on-write a hit waits on the disk.** This was the plan's largest open risk, and on ext4 it holds up where compile costs something: the web CMS's all-hit build is 0.10 of a plain build with every hit a plain copy. Where compile is nearly free, as on the generated repo, the copies are what is left: 0.37, at 71% iowait. Windows NTFS has no reflinks and is unmeasured, and so is a cold runner that first downloads every blob. See [Next](#next), item 5.
- Outputs of `mida build` carry `/_/` source paths, which may hurt debugging them locally. Untested. Plain `dotnet build` is unaffected.
- `IsAotCompatible` only checks our own calls. Publish a throwaway AOT console that references `Mida.Store` now and then to catch dependency drift.
