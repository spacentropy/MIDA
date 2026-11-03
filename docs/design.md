# MIDA design

Status: draft, started 2026-10-31. Background: [landscape report](research_notes/reports/NET%20monorepo%20tooling%20landscape.md), [cache on object storage](notes/build-cache-object-store.md). Implementation: [MVP plan](plan.md).

## 1. Goals

MIDA is a monorepo tool for .NET. It makes one repository with hundreds of projects feel as fast as a repository with one, and it gives the team a single place to see how builds, tests and releases are doing.

### 1.1 Superfast builds

- **Build only what changed.** Affected detection from the MSBuild project graph and the git diff, including package-version changes in Central Package Management.
- **Never build the same thing twice, on any machine.** A remote cache in plain object storage (GCS first, then S3 and Azure Blob), or in the CI provider's own cache (GitHub Actions cache) where a bucket is not wanted. A cache hit is indistinguishable from a build: the key is the MSBuild-evaluated inputs plus dependency reference-assembly hashes, and restored outputs work regardless of machine or checkout path.
- **One MSBuild invocation per run, not one per project.** Use the static graph so evaluation and startup costs are paid once.
- **Local first.** The same cache logic serves a developer's machine, so `mida build` after `git pull` is a cache restore, not a rebuild.

### 1.2 Superfast tests

- **Run only the tests that can be affected**, at project level first, with test-level impact as a later layer.
- **Cache test results** keyed the same way as build outputs. An unchanged test project with unchanged dependencies is not re-run.
- **Shard across CI runners** from the graph, balanced by recorded test durations.
- **Collect coverage without slowing the run**, merged across shards into one report per run.

### 1.3 Superfast publishing

- **NuGet.** Release units declared once (which projects ship together, how they are versioned). Pack and push in dependency order, only packages whose contents changed, with API-baseline checks before publish.
- **OCI containers.** Build and push images straight from the project graph (`dotnet publish` container support), only for services whose closure changed, with layer reuse across runs.
- **Promotion is a cache operation.** Artifacts built on a green commit are already in the cache; publishing moves them, it does not rebuild them.

### 1.4 Build and failure metrics

- **OpenTelemetry (OTEL) is the only metrics path.** MSBuild already emits OTEL traces; MIDA adds spans and metrics for cache hit/miss, affected selection, test shards and publish steps, and exports to any OTEL-compatible backend.
- **Nondeterminism detection.** Two different outputs for the same cache key are logged and surfaced, never silently overwritten.

### 1.5 Coverage

- **Per-project and per-release-unit coverage** merged across shards and runs.
- **Coverage diff on pull requests:** what the change covers and what it left uncovered.
- **Coverage history** exported as OTEL metrics, so a drop is visible when it happens.

### 1.6 Constraints we hold to

- **Zero per-project files.** csproj, slnx, slnf and dirs.proj are the only build description. Adoption adds nothing to projects. MIDA can emit an slnf of the affected set so the IDE and plain `dotnet build` work on the same scope.
- **MSBuild stays the executor.** `dotnet build`, Rider and Visual Studio keep working unchanged.
- **No server.** Cache and metrics live in a bucket the team already has. Concurrency uses the provider's conditional writes.
- **Ships as a dotnet tool.** One `dotnet tool install`, works on Linux, macOS and Windows. v1 is framework-dependent and loads the installed SDK's MSBuild through MSBuildLocator, so evaluation matches `dotnet build` exactly, including NuGet-delivered and workload SDKs. NativeAOT is deferred, not dropped (see [NativeAOT note](notes/nativeaot.md)): Microsoft.Build evaluates under AOT since 18.10, but the SDK's plugin resolvers do not load yet and the gain is about 400 ms of JIT warmup per run. Until then, every decision keeps the AOT path open:
  - `IsAotCompatible` is on for every MIDA assembly from day one and warnings are errors, so trim- and AOT-unsafe code cannot land.
  - No reflection-based plugin loading, runtime code generation or reflection-based serialization of our own. JSON goes through source generators.
  - Dependencies must be trim-clean or easy to replace. Prefer the BCL.
  - MSBuild is reached only through the evaluation object model, behind one seam, so MSBuildLocator can later be swapped for a bundled engine with in-process registered SDK resolvers.
- **CI-system agnostic.** Last-green ref per branch and affected matrix are MIDA outputs; GitHub Actions, Google Cloud Build, Azure Pipelines and GitLab consume them.

### 1.7 Non-goals (v1)

Remote execution, sandboxed builds, non-MSBuild languages, a new project format, replacing the CI system, hosting a dashboard or metrics backend.

## References and inspirations

- [Bazel / Blaze](https://github.com/bazelbuild/bazel): correctness-first builds, content-addressed remote cache, the graph as queryable data.
- [BuildXL](https://github.com/microsoft/buildxl): Microsoft-scale caching and distribution on top of MSBuild's static graph.
- [dotnet-affected](https://github.com/leonardochaia/dotnet-affected): affected detection from ProjectGraph and git diff, CPM-aware, emits slnf.
- [Nx](https://nx.dev/): the orchestration and CI workflow to match (affected, remote cache, agents) without the Node dependency.
- [MSBuildCache](https://github.com/microsoft/MSBuildCache): the project-cache plugin API and MSBuild-faithful input fingerprinting.
- [depot.dev](https://depot.dev/): fast remote container builds with persistent layer cache.
- [delta-build](https://github.com/dotTrench/delta-build): incremental builds for .NET.
- [Meziantou.DeltaBuild](https://github.com/meziantou/Meziantou.DeltaBuild): change-based project selection for .NET.
