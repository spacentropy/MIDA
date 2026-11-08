# MSBuild project-cache plugin

Status: spike result, 2026-11-06. Answers [plan milestone 2](../plan.md#2-plugin-spike-half-a-day). Run on macOS arm64: three chained projects on SDK 11.0.100-rc.3, then a private monorepo of 49 `net8.0` projects built with SDK 10.0.403. Keys were not computed: a project's stored entry was served as a hit unless the spike was told to miss it. Follows on from [relocation.md](relocation.md).

## Answer

The plan's integration works. Both hooks fire for every project without `/reportfileaccesses`, target results replay correctly at real scale, and a miss builds against a dependency's restored reference assembly. No refresh pass and no timestamp changes are needed.

The plan's cache key was unsound, though: **it served stale dependency code, and CI stayed green**. The fix is below and is now part of the plan.

| Real repo | Result |
|---|---|
| Record run | 49 misses, 49 `HandleProjectFinishedAsync` calls, 26.0 s (no visible overhead against 22 to 28 s plain) |
| All hits, next commit, other path | 49 hits, 0 compiles, 0 targets run |
| Time for that hit, end to end | 3.6 s: restore 1.8, clone 1.2, graph evaluation and hits 0.6 |
| Unit tests on the hit, `--no-build` | 3,441 passed, 117 skipped, 0 failed |
| One project misses, 48 hit | 1 compile, 15 targets run, 0.9 s |
| Replay data | 6.7 KB at most per project, about 200 KB in all |

## Stale dependency copies

The key uses dependencies' reference assemblies, so a method body edit does not change a dependent's key. That early cutoff is right for compiling and wrong for `bin/`: a dependent's `bin/` holds a copy of each dependency's implementation.

On the real repo, a behaviour change in one shared library left its reference assembly unchanged, so all its dependents would hit:

| State | Unit tests |
|---|---|
| Plan as written, 26 stale copies in `bin/` folders | 3,441 passed, **0 failed** |
| After replacing those 26 copies | 3,440 passed, **1 failed**: the test covering the edit, correctly |

Keying on implementation assemblies instead would be sound, but it loses the cutoff: the same body edit would recompile 14 projects instead of 1 ([relocation.md](relocation.md)), and up to 31 for the most-referenced libraries.

**Fix: dependency references in the manifest.** When storing a project, a file in its `bin/` that is byte-equal to a file a transitive project dependency produced is stored as a reference to that dependency, not as a blob. On a hit, the reference resolves to the dependency's current file. Content addressing makes the match cheap. The spike found 784 references across 45 projects on the real repo.

The reference must resolve on every hit, not only when the dependency misses. An entry written before a dependency's edit can meet a dependency that also hits, from a newer entry, so both are hits and the combination is still stale. The spike only exercised the miss case.

Hits are served in topological order: a dependent's request came after its dependency finished, so the dependency's current files exist when the references resolve.

**Test caching must key on implementation hashes.** A test project's outcome depends on dependency code, not API shape, so the ref-based key cannot decide whether to skip a test run.

## Using the API

In-process, after `MSBuildLocator.RegisterDefaults()`:

1. `new ProjectCollection(globals)`, then `new ProjectGraph(new ProjectGraphEntryPoint(solution, globals), collection)`.
2. `BuildParameters` with `ProjectCacheDescriptor.FromInstance(plugin, settings)`, `GlobalProperties`, `Loggers`, `MaxNodeCount`, `EnableNodeReuse = false`.
3. `BuildManager.DefaultBuildManager`: `BeginBuild`, `BuildRequest(new GraphBuildRequestData(graph, ["Build"]))`, `EndBuild`.

Observed behaviour:

- Each project gets one `GetCacheResultAsync`. Referenced projects are asked for `Build` plus `GetTargetFrameworks`, `GetNativeManifest`, `GetCopyToOutputDirectoryItems` and `GetTargetFrameworksWithPlatformForSingleTargetFramework`, while entry projects get `Build` alone. Results must be stored for all requested targets, and with them no target runs on a hit.
- `HandleProjectFinishedAsync` is not called for hits. Its `FileAccessContext.Targets` lists the targets, and `BuildResult.ResultsByTarget` holds 2 to 6 entries.
- Replayed items contain absolute paths, so **`{RepoRoot}` belongs in the stored target results**, even though output files need no rewriting. The spike only replaced the repo root. Milestone 5 added `{NuGetPackageRoot}`, and a clone on another NuGet root hits.
- `ITaskItem.CloneCustomMetadata()` must be read through its keys, because its entry type is not `DictionaryEntry`.
- An exception in a hook fails the build, surfacing from `EndBuild`. The real plugin must catch store failures and treat them as a miss.
- Target-result JSON ties an entry to a target list. Keep requested targets in the key.

## SDK and runtime support

SDK 10 and 11 only, the supported versions. Projects may target older frameworks: `net8.0` projects built under SDK 10 passed everything above.

- The plugin API in `Microsoft.Build.ProjectCache` is identical in MSBuild 18.9 (SDK 10) and 18.12 (SDK 11), so one source tree serves both. SDK 8 had only `Microsoft.Build.Experimental.ProjectCache`.
- A host cannot load MSBuild from an SDK newer than its runtime. Both builds of the tool are needed: the `net11.0` build loaded SDK 10 and 11, the `net10.0` build loaded SDK 10.
- **A `net10.0` host in a repo pinned to SDK 11 silently picks SDK 10** and fails with `NETSDK1045`, because the Locator filters out SDKs it cannot load and ignores `global.json`. Since milestone 6, `mida build` compares the loaded SDK with the one `dotnet --version` resolves in the solution's directory, and stops with an error when they differ.
- `Directory.Build.props` now multi-targets, with `runtime-async` on `net11.0` only.

## Not tested

[Milestone 4](local-store.md) covered hits decided by real keys, a hit resolving references to another hit, dependency content files, and Linux.

- Overlaying on a dependent's `deps.json`, which was unchanged here. A dependency's package change alters the dependent's restore output, so it should change the key.
- Windows. Release builds since 4b, on the toy repo only.
- A machine with only runtime 10. With runtimes 10 and 11 installed, the tool installed through `dotnet tool install` ran its `net11.0` build, since it built a repo pinned to SDK 11.
