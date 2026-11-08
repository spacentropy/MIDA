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

Every run includes restore, so the rows compare. One iteration each; run it again if a number looks off. Multi-targeting is off in the benchmark. Its dependents get keys since plan 4b item 1, but the generator's `net10.0;net11.0` needs SDK 11, which only the `net11.0` host loads, and the numbers below are on SDK 10.

Run: `dotnet test --project tests/Mida.Tests -f net10.0 --treenode-filter "/*/*/Benchmarks/Build128" --output Detailed`. Each size writes `TestResults/bench-<N>.md`; paste it below. 1024 takes about five minutes on a laptop.

## Numbers

macOS 27.2.0 arm64 (APFS), SDK 10, Debug. The 1024 column is from 2026-11-08, after the stall fix below. The 8 and 128 columns are from 2026-11-07 and predate the restore fix below, so they overstate restore. The generated projects are one class each, so compile is nearly free and these numbers are the cache's overhead, not its saving. The real saving shows on a repo whose compile costs something: see [local-store.md](local-store.md) and plan 4b item 4.

| scenario | 8 | 128 | 1024 |
|---|---|---|---|
| dotnet build, cold | 0.7 s | 3.9 s | 27.1 s |
| mida build, empty cache | 1.8 s | 9.1 s | 40.1 s |
| mida build, clean clone, all hits | 0.8 s | 5.2 s | 25.4 s |
| body edit of the widest leaf, 1 miss | 0.8 s | 5.1 s | 25.8 s |
| API edit of the widest leaf, closure misses | 1.3 s (5) | 6.6 s (23) | 27.6 s (74), was 62.0 s |
| cache size | 39 MB | 47 MB | 111 MB |
| key computation | 0.01 s | 0.2 s | 2 s |

Findings:

- The first 1024 run had an all-hit run slower than a cold `dotnet build` (33 s against 27 s). The phases were restore 21.5 s, graph evaluation 6.4 s, prediction 0.9 s, graph build with 1024 hits 6 s. The in-process restore had no `MaxNodeCount`, so it ran on one node; with `ProcessorCount` it takes 11 s, the same as `dotnet restore`, and the table above is after that fix.
- MSBuild's floor on this repo is most of every row: a warm `dotnet build --no-restore` with nothing to compile takes 18.8 s, and compile is 2 to 3 s of the cold build. A cache can only remove compile, so here an all-hit run can at best be a few seconds under cold. Overhead per project: about 11 ms to evaluate, 6 ms per hit in the build including the key. If the benchmark should show a saving rather than a floor, give the generator a classes-per-project knob.
- A miss costs about 14 ms per project over a plain build at 1024 (43 s against 29 s). Storing is the likely cost.
- **74 misses among 950 hits took 62 s, slower than 1024 misses (43 s).** Fixed 2026-11-08: now 27.6 s, against 25.4 s for all hits. It was not superlinear work but two thread-pool starvations, found with `dotnet-dump` during the stall. In both, the CPU was idle and every pool thread waited on an MSBuild lock. Each hit's completion callback queues a pool item that takes that lock, so a burst of hits fills the pool, and the pool grows by about one thread per half second. Repeated runs swung between 30 s and 105 s.
  - `BuildManager.HandleResult` waits synchronously on the plugin's `HandleProjectFinishedAsync` while it holds the lock. The save awaited pool threads, which were queued behind that lock. The hook now captures target results and returns, the save runs in the background, and `EndBuildAsync` awaits every save. A project sharing a directory with another still saves inline, because a dependent may already write there. The hook's token is cancelled when the build ends, so background saves run without it.
  - MSBuild starts worker nodes inside the same lock, and on Unix each pipe handshake read completes on a pool thread. The first miss after many hits needed nodes, so node startup stalled. `mida build` now starts every node before the graph build, through a throwaway project that asks for one request per node. The nodes stay up for the session. An all-hit run pays node startup, about 1 s at 1024 projects. The project's temporary directory must outlive the nodes, because it is their working directory and evaluation fails once it is gone.
  - Rejected: `DOTNET_SYSTEM_NET_SOCKETS_INLINE_COMPLETIONS=1` fixed the stall but leaked to child processes, and the Roslyn compiler server deadlocked on it. Inline, a continuation did a synchronous read of its own pipe. A higher thread-pool minimum (`DOTNET_ThreadPool_ForceMinWorkerThreads` 128 to 1100) made runs slower, up to 156 s.

## Real-world repos

Same scenarios 0 to 3, pointed at a checkout, with one generic partial edit: append a comment line to a `.cs` in a leaf project. That changes the key but not the reference assembly, so it checks early cutoff without knowing the repo. API edits need repo knowledge, so skip them there. Not built yet; the generated sizes come first.

Two filters: the repo must build with SDK 10 or 11 (`global.json`), and Arcade repos (aspire, runtime, roslyn, efcore, aspnetcore) bootstrap their own SDK through `eng/build.sh`, so they cost weeks, not an afternoon. `dotnet/runtime` is out for good: native code, custom build. Aspire is possible later with its pinned SDK. Plain-SDK repos of real size to try first, in order: the 49-project repo the plan already names, OrchardCore, semantic-kernel `dotnet/`, OpenTelemetry .NET, Akka.NET. Their SDK pins and project counts are unchecked.
