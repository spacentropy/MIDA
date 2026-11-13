# Cache multi-targeted projects

Status: plan, 2026-11-13, revised the same day after two reviews (Fable and Codex, read-only, against `6079395`). Not built. Reverses an MVP scope line: [plan.md](../plan.md) lists "caching multi-targeted projects themselves" as out.

## Why

A project with several frameworks in `TargetFrameworks` gets no key and builds normally on every run ([cache-key.md](cache-key.md)). Its dependents get keys since plan 4b item 1. `mida build` reports only keyed projects, so the run looks like all hits.

`RealWorld/Abp` 10.7.1: 107 of the 270 projects in `framework/Volo.Abp.slnx` target `netstandard2.0;netstandard2.1;net8.0;net9.0;net10.0`, among them `Volo.Abp.Core`, `Caching` and `MongoDB`. A filled cache gives "163 hits, 0 misses" and still compiles those 107 projects five times each. Measured 2026-11-13 in the OrbStack container, four pinned cores, ext4, a clean clone at another path:

| step | time |
|---|---|
| stored graph found and checked, restore skipped | 1.0 s |
| MSBuild's own graph evaluation | 6.0 s |
| `build`: 163 hits and the 107 unkeyed projects | 46.0 s |
| total | 53.0 s, against 49.2 s for a plain build on the same bed |

Restoring outputs is not the cost: the hits write 4.3 GB, 68,723 files of 3,279 distinct contents, and `cp` of all of `bin/` and `obj/` takes 9 s there. Every compiler warning in the filled build's output comes from a multi-targeted project.

On GitHub Actions the same repo is 0.80 of a plain build with a filled cache; OrchardCore, whose projects all name one framework, is 0.09.

A second build in the same clone did not reuse the stored graph either: "framework/src/Volo.Abp.AI.Abstractions/bin changed", 20 s of restore and 8 s of evaluation. That is not this plan's: the probe asks whether directories exist and what they list before any output is restored (`BuildRunner.cs:82`, `Questions.cs:127`), and a hit creates `bin/` as a compile does. `GraphStore.NextKey` stores a second graph under the new answers, so it costs one evaluation per clone. Measured on its own if at all.

## How MSBuildCache does it

Read from `microsoft/MSBuildCache` at `54bcfb2`. Every graph node is an entry, named by project path and global properties (`NodeDescriptorFactory`), so each inner build and the outer build are cached apart. Querying an outer build materializes its inner builds' outputs first (`MSBuildCachePluginBase.GetCacheResultRecursivelyAsync`). Outputs are what each node's processes were seen to write (`/reportfileaccesses`, Detours, Windows only), so inner builds that share `obj/` are told apart by observation. mida predicts instead, on every platform, so the split between inner nodes is the work here.

## What exists

The one-framework case, built 2026-11-12, has every part for one inner node:

- Nodes are looked up by path and `TargetFramework` (`GraphModel.keyed`, `MidaCachePlugin.GetCacheResultAsync`). An inner node's name ends in ` (net10.0)`, and so does its record file, `<project>.<tfm>.mida-restored.txt`.
- The outer node is keyed: its own inputs and its inner nodes' reference assemblies, through `KeyComputer.AddDependencies`. Its entry holds target results only. `ProjectOutputs.Scan` returns nothing for it, and a miss of it clears nothing.
- A build-time reference to such a project adds the keys of both nodes.
- The walk and MSBuild's graph scheduler both finish a node's references first, and an outer node references its inner nodes, so inner nodes are served before their outer node. MSBuildCache's recursive materialization is not needed.
- A miss deletes only what its own node's record lists (`ProjectCache.Miss`), so it cannot delete a sibling's files unless they got into this node's entry.

The outer `Build` target declares no returns, `DispatchToInnerBuilds` returns `InnerOutput` and `GetTargetFrameworks` the negotiation metadata (`Microsoft.Common.CrossTargeting.targets`). Replaying what the build returned, as `TargetResults` does, is right; nothing is to be synthesized.

The gate is one filter, `GraphModel.cs:84`: a path is keyed when it has one node, or exactly one outer and one inner node.

## Found by the reviews

1. **An outer node that packs loses its package on a hit, today.** `_PackAsBuildAfterTarget` runs when `GeneratePackageOnBuild` is true and `IsInnerBuild` is not (`NuGet.Build.Tasks.Pack.targets:163-172` in SDK 10.0.404), so in the outer build. Since 2026-11-12 a project with one framework in `TargetFrameworks` and `GeneratePackageOnBuild` has a keyed outer node whose hit replays target results and writes no `.nupkg`. Storing the package would not do: the outer key hashes its inner nodes' reference assemblies, not their keys (`KeyComputer.cs:174`), so a body edit would restore an old package.
2. **One boundary per inner node is missing.** Capture (`Scan`: output directories, then `Owns` or `FileListAbsolute.txt`), restore acceptance (`Accept`), the record (`Restored`) and cleanup (`Hit`, `Miss`) each decide what belongs to a node from `OutputDirectories` or `OwnedRoots`. `OwnedRoots` are `BaseOutputPath` and `BaseIntermediateOutputPath`, the same for every inner node of a project. Narrowing one of them and not the others either leaves obsolete files or deletes a sibling's.
3. **It is a race, not only an inclusion.** A node that does not share saves on a background task after the hook returns (`ProjectCache.cs:227`), while its siblings and then its outer node still build. A scan that reaches a directory they write takes whatever is there at that moment: entries that differ between runs and "another build stored different outputs".
4. **Two inner nodes with one framework throw.** `keyed` is a `ToDictionary` on path and framework (`GraphModel.cs:86`). A dependent that sets `SetPlatform` or a runtime identifier makes a second inner node with the same `TargetFramework`. Today the count filter hides it.
5. **A dependent can copy a sibling's file.** `KeyComputer.DependencyCopies` walks every reachable inner node, ordered by project path only, and takes the first file of matching name and bytes (`KeyComputer.cs:75`). A `data.txt` equal in two frameworks, then changed only in the one a dependent uses, can let the dependent hit with the other's copy. Exists today for every multi-targeted dependency; the tests edit files shared by both frameworks, so they miss it.
6. **An all-hit build never replays through MSBuild.** It ends before MSBuild loads (`BuildRunner.cs:134`), so the clean-clone tests do not exercise the outer node's replayed results. Only a build with both hits and misses does.

## Plan

1. **Leave an outer node that packs unkeyed.** At evaluation, a cross-targeting node with `GeneratePackageOnBuild` true gets no key, nor do its inner nodes. Fixes finding 1 for the one-framework case now. Test: a one-framework `TargetFrameworks` library with `GeneratePackageOnBuild`, a clean clone at another path, the `.nupkg` exists and equals a plain build's.
2. **Report the unkeyed.** `mida build` says how many nodes got no key and why, by kind ("multi-targeted", "packs in the outer build", "not computable"), so "163 hits" no longer reads as everything.
3. **Spike: what an inner node predicts and writes.** For the default layout, `AppendTargetFrameworkToOutputPath=false`, a custom `OutDir` and `IntermediateOutputPath`, `PackageOutputPath`, and `DocumentationFile`, on a `net10.0;netstandard2.0` library and on Abp's `Volo.Abp.Core`: print each inner node's `OutputDirectories`, `OwnedRoots` and `FileListAbsolute.txt` paths, and diff the files under `bin/` and `obj/` after each inner build alone. Also check whether Abp's projects set `ProduceReferenceAssembly` for `netstandard2.0`.
4. **One boundary per inner node.** From the spike: a set of directories that an inner node alone writes, used by capture, acceptance, the record and cleanup alike. `obj/<config>/<tfm>/` must stay in it, since `Written` finds `FileListAbsolute.txt` there. A layout where two inner nodes of a project write one directory (`AppendTargetFrameworkToOutputPath=false`) leaves the path unkeyed.
5. **Open the gate.** Key a path with one outer node and inner nodes whose frameworks are all distinct; otherwise none of its nodes. Fixes finding 4.
6. **Pick the copy by the dependent's framework, or refuse the ambiguity.** For finding 5: either resolve which inner node a reference resolves to (`SetTargetFramework`, or the nearest framework), or give a dependent no hit when two reachable inner nodes offer a file of one name. Choose after step 3; the second is smaller.
7. **Real repos.** `RealWorld/Abp` and `RealWorld/OrchardCore` in the container, at least twice each, then on CI.
8. **Benchmark.** `GeneratedRepo` makes every `multiTargetEvery`-th library `net10.0;net11.0`, which needs SDK 11, and its libraries reference `net10.0`-only ones, so `netstandard2.0` would not compile. Either make every library below a multi-targeted one multi-targeted too, or move the benchmark to `-f net11.0`. Its hit and miss expectations count projects (`Benchmarks.cs:108`) and become node counts. Turn on `ProduceReferenceAssembly` if early cutoff is measured.
9. **Docs.** `cache-key.md` (the "gets no key" paragraph), `plan.md` (the scope line and the known limit), `relocation.md` if the boundary changes what a scan takes, and this note's status.

Steps 1 and 2 stand alone and go first. Steps 3 to 6 come before the gate is relied on.

## Checks

TUnit, `CacheEndToEndTests`, on net10.0 and net11.0:

- The existing `multiTarget` cases (`LibB` on `net10.0;netstandard2.0`) change expectations: `LibB`'s three nodes hit in a clean clone at another path, and its tests pass on restored outputs.
- A body edit of `LibB` misses its two inner nodes alone and its dependents hit, as exact counts, as `OneFrameworkInTargetFrameworksIsCached` does. An API edit misses them all.
- No entry of an inner node holds a file of another. The fixture needs inner builds that write files with framework-free paths into a shared directory, such as a `DocumentationFile` or a package path, or a library with disjoint `bin/Debug/<tfm>/` passes trivially.
- A miss of one inner node leaves its siblings' restored files in place and byte-equal to a plain build's, and the next build of the sibling hits.
- A revision switch: build, change which files one framework writes, build again, and no file of the old revision stays.
- Per-framework content: a file conditioned on the framework, equal in both at first, then changed in one. The dependent on that framework misses or copies the new bytes.
- A dependent with `SetPlatform`, making two inner nodes with one framework: the build succeeds and that path is unkeyed.
- Replay in a mixed build: one dependent misses while `LibB`'s nodes hit, so MSBuild replays the outer and inner results. Its items and metadata equal a plain build's, including through a reference with `SetTargetFramework`.
- Step 1's package check, for one framework and for two.

## Risks

- `netstandard2.0` without `ProduceReferenceAssembly` has no reference assembly, so its dependents are keyed by its implementation and lose early cutoff.
- Dependents' keys include every inner node of a dependency, so an edit to `netstandard2.0` code alone misses a `net10.0` dependent. Unchanged by this plan.
- An imported target that only one framework reads can change `GetTargetFrameworks` metadata without changing an assembly. The outer key holds its own inputs, which include imports the outer evaluation reads; one only an inner evaluation reads is not in it. Not checked.

## Not in this plan

- Storing an outer node's own files, such as the package.
- Observing writes, as MSBuildCache does.
