# Benchmarks and the test harness

Status: built 2026-11-07. Code: `tests/Mida.Tests/GeneratedRepo.cs` (generator), `GeneratedRepoTests.cs` (correctness, in the suite), `Benchmarks.cs` (timing, `[Explicit]`).

## One generator, two consumers

`GeneratedRepo.Generate(root, count, seed, multiTargetEvery, testEvery)` writes a seeded layered DAG: `log2(count)` layers, each library references one or two libraries from lower layers, every fourth project above the bottom layer is a TUnit test project over one library below it, and every `multiTargetEvery`th library is `net10.0;net11.0`. The same seed gives the same graph on every machine, so a second checkout is a clean clone.

Every library's `Name()` nests its dependencies' names: `P5[P2[],P0[]]`. Each test asserts the full expected string, which the generator computes from the graph. That makes one graph the oracle for everything:

- `BodyEdit` changes a string literal: the key of that one project changes, and exactly the tests over its dependent closure fail.
- `ApiEdit` adds a public member: the reference assembly changes, so the keys of the whole dependent closure change, and no test fails.
- `Dependents(project)` is that closure, so tests assert exact hit counts and exact failing test projects, not "some hits".
- Later, `mida test` must run exactly the test projects in the closure of what changed. The same oracle covers it.

The hand-written `TestRepo` stays for the targeted scenarios (analyzer dependency, copied files, shared `OutDir`, web assets). Replacing it would be more code for nothing.

## Scenarios

| # | Scenario | Test asserts (8 projects, in the suite) | Benchmark records (8, 128, 1024) |
|---|---|---|---|
| 0 | `dotnet build`, cold checkout | | wall time, the baseline |
| 1 | `mida build`, empty cache | N misses | wall time, cache size |
| 2 | clean clone at another path | N hits, every test passes | wall time, the headline number |
| 3 | partial: {body, API} × {leaf, mid, root} edit | exact miss count, exact failing tests | leaf only: wall time, hits/misses |
| 4 | later: `mida test` after the same edits | exactly the closure's test projects run | |

Every run includes restore, so the rows compare. One iteration each; run it again if a number looks off. Multi-targeting is off in the benchmark until plan 4b item 1 lands, because today one multi-targeted library turns the cache off above it.

Run: `dotnet test --project tests/Mida.Tests -f net10.0 --treenode-filter "/*/*/Benchmarks/Build128" --output Detailed`. Each size writes `TestResults/bench-<N>.md`; paste it below. 1024 takes about five minutes on a laptop.

## Numbers

macOS 27.2.0 arm64 (APFS), SDK 10, Debug, 2026-11-07. The generated projects are one class each, so compile is nearly free and these numbers are the cache's overhead, not its saving. The real saving shows on a repo whose compile costs something: see [local-store.md](local-store.md) and plan 4b item 4.

| scenario | 8 | 128 | 1024 |
|---|---|---|---|
| dotnet build, cold | 0.7 s | 3.9 s | 26.7 s |
| mida build, empty cache | 1.8 s | 9.1 s | 51.2 s |
| mida build, clean clone, all hits | 0.8 s | 5.2 s | 33.4 s |
| body edit of the widest leaf, 1 miss | 0.8 s | 5.1 s | 33.5 s |
| API edit of the widest leaf, closure misses | 1.3 s (5) | 6.6 s (23) | 84.2 s (74) |
| cache size | 39 MB | 47 MB | 111 MB |
| key computation | 0.01 s | 0.2 s | 2 s |

Findings:

- At 1024 projects an all-hit run is slower than a cold `dotnet build`. Restore is in both, so the difference is store and plugin overhead of roughly 7 ms per project on top of MSBuild. On real projects the compile it skips is far larger, but this is the floor.
- A miss costs about 24 ms per project over a plain build at 1024 (51 s against 27 s). Storing is the likely cost.
- **74 misses among 950 hits took 84 s, slower than 1024 misses (51 s).** Something in the miss-after-hit path is superlinear: candidates are `RemoveObsolete`, the dependency-files scan, or the once-per-run blob check in `OutputCache.RestoreAsync`. Not investigated. First thing to profile before 4b item 4.

## Real-world repos

Same scenarios 0 to 3, pointed at a checkout, with one generic partial edit: append a comment line to a `.cs` in a leaf project. That changes the key but not the reference assembly, so it checks early cutoff without knowing the repo. API edits need repo knowledge, so skip them there. Not built yet; the generated sizes come first.

Two filters: the repo must build with SDK 10 or 11 (`global.json`), and Arcade repos (aspire, runtime, roslyn, efcore, aspnetcore) bootstrap their own SDK through `eng/build.sh`, so they cost weeks, not an afternoon. `dotnet/runtime` is out for good: native code, custom build. Aspire is possible later with its pinned SDK. Plain-SDK repos of real size to try first, in order: the 49-project repo the plan already names, OrchardCore, semantic-kernel `dotnet/`, OpenTelemetry .NET, Akka.NET. Their SDK pins and project counts are unchecked.
