# Relocating build outputs between checkouts

Status: spike result, 2026-11-06. Answers [plan milestone 1](../plan.md#1-relocation-spike-half-a-day). Run on macOS arm64, checkouts at different paths: three chained projects (App → LibA → LibB) on SDK 11.0.100-rc.3, then a real repo of 49 projects on the .NET 8 SDK ([results](#real-repo)).

## Answer

Outputs built at one path work at another, and across commits. On CI a hit takes two steps:

1. Run restore in the target checkout. Restore-owned files stay out of the cache.
2. Materialize the cached files.

`mida build` injects two properties, and later CI steps use `--no-build`. On the real repo, `dotnet test --no-build` then ran the unit tests on the relocated binaries with the same outcomes as on the writer. No file contents are rewritten and no timestamps are changed.

MIDA targets CI build and test time. Local incremental builds already work, so making a plain `dotnet build` a no-op after `mida build` is not a goal. A plain build after a hit recompiled 49 of 49 projects, in 19.1 s, which is why CI must use `--no-build`.

## What goes in the manifest

Everything under each project's `bin/` and `obj/`, except:

| Excluded | Why |
|---|---|
| `project.assets.json`, `*.nuget.*`, `project.nuget.cache` | Owned by restore, hold absolute paths |
| `*.assets.cache`, `*AssemblyReference.cache` | Incremental-build caches, not needed on a hit |
| `*.FileListAbsolute.txt` | Holds the writer's absolute paths. Only `dotnet clean` and incremental clean read it |
| `*.CoreCompileInputs.cache`, `*.GeneratedMSBuildEditorConfig.editorconfig` | Added in [milestone 4](local-store.md): differ per checkout, and only an incremental build reads them |

**Correction, milestone 4:** "No file contents are rewritten" holds only without static web assets. Their manifests hold absolute content roots, so such a project is [not stored](local-store.md#decisions). The real repo's unit tests did not surface this, because the writer's checkout still existed and they do not start the web apps.

## Injected properties

`mida build` passes these as global properties on every run, on CI and on laptops:

```
ContinuousIntegrationBuild=true
IncludeSourceRevisionInInformationalVersion=false
```

Nothing is added to the repo, so plain `dotnet build`, Rider and local debugging keep their defaults. They are injected on every run, so every run computes the same keys for the same inputs: global properties are in the key.

At the same commit, 1,624 of 1,624 DLLs and PDBs were byte-identical between injected properties and the same properties in `Directory.Build.props`. Neither property needs to be set by hand. `Deterministic` is on by default and `ContinuousIntegrationBuild` produces `PathMap`.

- **`ContinuousIntegrationBuild`.** Without it, implementation DLLs and PDBs differ between checkouts and a relocated DLL reports the writer's path in stack traces (`/home/runner/...` on a laptop, for example). With it, every compared file (DLLs, PDBs, apphost, `deps.json`, `runtimeconfig.json`) is byte-identical across checkouts, and paths read `/_/LibB/B.cs`.
- **`IncludeSourceRevisionInInformationalVersion=false`.** The SDK writes the commit SHA into `AssemblyInformationalVersion`, and that attribute is emitted into the reference assembly. So a recompile on a new commit changes `obj/ref` even with no API change. Since `obj/ref` is in dependents' keys, the early cutoff from reference assemblies would be lost: a body-only edit compiled 3 projects with the SHA and 1 without.

Cost of the second property: `InformationalVersion` reads `1.0.0` with no commit. That is wrong for pack and publish, which must not inject it when those commands exist.

## Real repo

A private monorepo: 49 projects in the solution, five of them `Microsoft.NET.Sdk.Web`, 3.8 GB of outputs, 3,558 unit tests. B one commit ahead at a different path, same NuGet root.

| Check, restore and copy only | Result |
|---|---|
| Unit tests with `--no-build` in B | 3,441 passed, 117 skipped, 0 failed, same outcomes as the writer per test name |
| Plain `dotnet build` afterwards | 49 of 49 recompile, 19.1 s |

Time per phase, in seconds:

| Phase | Cold build | Hit |
|---|---|---|
| Restore | Included in build | 2.0 |
| Build | 21.6 and 27.7 in two runs | None |
| Materialize | None | 18.0 and 18.9 with `rsync`, 0.9 with `cp -c` |

A hit totals about 3 s with clones, against 22 to 28 s cold. That is a sum of phases, because the clone was timed separately. A plain copy took about as long as the compile, so **a local hit needs copy-on-write materialization**. A compile on this machine is fast, so the gain should be larger on slower CI runners, but that was not measured.

The outputs hold 1,234 unique blobs in 3,766 files: 480 MB of unique content in 3.8 GB, a 7.9x saving, because the same DLLs land in many `bin/` folders. Per-file content addressing is therefore worth it for both store size and download time. No output landed outside `bin/` and `obj/`.

Cross-commit hits keep the writer's embedded SourceLink commit. The sources behind a hit are identical by key, so the links resolve to the same content. The commit is why outputs built at different commits differ: it reaches PDBs and so the DLLs' debug info, but not reference assemblies.

## Not needed: making a plain build a no-op

This was the spike's original target. It is recorded so nobody rediscovers it, or for if the goal returns.

It works: restore, materialize, a refresh pass, then one shared timestamp on every file. The refresh runs target `Compile` with `SkipCompilerExecution=true;BuildingProject=true;BuildProjectReferences=false`. On the real repo that gave 0 of 49 recompiles, a second build matching a true no-op at 491 targets, and 3,403 files identical to the writer's. The refresh cost 4.0 s, about one no-op build. Later edits rebuilt exactly the right projects, checked as a set against the project graph: 1 for a method body, 14 for a public type, the library's transitive dependents.

Why it is needed: `CoreCompile` lists `*.CoreCompileInputs.cache` as an input. That file is a hash of `@(Compile)`, `@(ReferencePath)` and a few properties, and `ReferencePath` holds absolute paths, so the hash differs per checkout for any project with a project reference, and for every project on a different NuGet root. `GeneratedMSBuildEditorConfig.editorconfig` and `sourcelink.json` are also inputs and hold paths or the commit.

Traps: `Hash` is order-sensitive, so a hand-picked target list produced a wrong hash and made things worse. Under a bare `-t:` the target's `BuildingProject` condition is false, so it silently does nothing. `Microsoft.Build.Tasks.Hash` is public and reproduced a real build's hash from a captured item list, while a Python reimplementation did not match.

## Not tested

- Hits through the plugin. This spike copied files and ran `dotnet test` by hand. [Milestone 2](project-cache-plugin.md) covered that, including a project that misses while its dependencies hit.
- Windows. Linux is covered by a small repo in [milestone 4](local-store.md).
- Release configuration on the real repo, whose runs were Debug. The toy repo has hit in Release since plan 4b.
- Copy-on-write outside APFS. ext4 has no reflinks, so a plain copy may dominate there.
- `/_/` source paths in a debugger, for someone debugging `mida build` outputs. Plain `dotnet build` is unaffected.
- Different NuGet roots with packages used at runtime. The real repo shared one root. Since milestone 5 a toy clone on another root hits and its tests pass, but its package is referenced, not called.
- Integration tests of the real repo, which need external services.
- The nine real-repo projects outside the solution.
