# Benchmarks and the test harness

Status: built 2026-11-07, CI jobs added 2026-11-10. Code: `tests/Mida.Tests/GeneratedRepo.cs` (generator), `GeneratedRepoTests.cs` (correctness, in the suite), `Benchmarks.cs` (timing, `[Explicit]`), `.github/workflows/bench.yml`.

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

Every run includes restore, so the rows compare. Multi-targeting is off in the benchmark. Its dependents get keys since plan 4b item 1, but the generator's `net10.0;net11.0` needs SDK 11, which only the `net11.0` host loads, and the numbers below are on SDK 10.

Since 2026-11-10 each scenario is its own test: `Fresh`, `Unchanged`, `BodyEdit` and `ApiEdit`. Each one is shaped like a CI run:

- The edits and `Unchanged` start from a clean clone at another path, with a cache that an earlier build filled.
- Build servers are shut down before every timed run, because a runner starts with none.
- A plain `dotnet build` of the same repo is timed beside it, and the number to compare is the ratio `mida / dotnet`. Seconds differ between machines, the ratio far less.
- Both times are the fastest of three. Between single runs of one commit the ratio moved by 7% on a laptop. With the fastest of three, two runs at 1024 projects agreed within 1%, and one scenario takes about four and a half minutes there.
- The test fails unless the run hits and misses exactly the projects the graph says.

Run: `MIDA_BENCH_COUNT=1024 dotnet test --project tests/Mida.Tests -f net10.0 --treenode-filter "/*/*/Benchmarks/*" --output Detailed`, or name one scenario in place of the `*`. The count is 128 unless set. Each scenario writes `TestResults/bench-<scenario>-<count>.md`. `MIDA_BENCH_TOOL` names an installed mida to run in place of the build beside the tests.

## In CI

`.github/workflows/bench.yml` runs the four scenarios at 1024 projects, one job each, on every push to `main` that changes more than docs. Each job packs mida and installs it with `dotnet tool install`, so the tool package is what gets measured. The job summary shows the ratio beside that of the last green run on `main`, which it reads from that run's artifact. That column is how to tell whether a commit made mida faster, as long as both runs had the same CPU model. Each result stores its model in `bench-<scenario>-<count>.cpu`. When the two differ, the column names the other model, and the difference says nothing about the commit.

Each summary also says what the runner is: CPU model, memory, disk and how fast that disk writes and reads. For each of the three repetitions it says what the whole machine did meanwhile: CPU busy, iowait and steal, and the memory left.

First numbers, 2026-11-10, Ubuntu 24.04 x64 with 4 processors, two runs of commit `6a619fd`, from before the summary said what the runner is:

| scenario | dotnet build | mida build | mida / dotnet | the same commit again |
|---|---|---|---|---|
| Fresh | 114.2 s | 113.1 s | 0.99 | 1.22 |
| Unchanged | 119.4 s | 84.7 s | 0.71 | 0.71 |
| BodyEdit | 139.5 s | 108.0 s | 0.77 | 0.72 |
| ApiEdit | 116.4 s | 87.8 s | 0.75 | 0.71 |

`Unchanged` held, the edits moved by 6 to 8%, and `Fresh` moved by 23%: its `mida build` took 143.7 s in two runs and 113.1 s in one, against a plain build of 114 to 118 s in all three.

The next run, of commit `b4c2bc5`, has the readout. Since `6a619fd` only the benchmark changed, not mida:

| scenario | CPU | disk writes | dotnet build | mida build | mida / dotnet | the run before |
|---|---|---|---|---|---|---|
| Fresh | AMD EPYC 7763 | 405 MB/s | 117.2 s | 146.9 s | 1.25 | 1.22 |
| Unchanged | AMD EPYC 7763 | 412 MB/s | 117.7 s | 84.4 s | 0.72 | 0.71 |
| BodyEdit | AMD EPYC 7763 | 399 MB/s | 120.6 s | 85.8 s | 0.71 | 0.72 |
| ApiEdit | AMD EPYC 9V45 | 186 MB/s | 112.1 s | 94.3 s | 0.84 | 0.71 |

The three repetitions in one job agree: `Fresh` took 146.9, 147.0 and 151.7 s, `Unchanged` 84.4, 84.7 and 84.4 s. The differences are between runners:

- The three jobs on the EPYC 7763 repeated the run before within 3%. The job on the EPYC 9V45, whose disk writes half as fast, moved by 18%.
- The ratio does not cancel the hardware out, because the two builds load it differently. On the EPYC 7763 a plain build kept the processors 88% busy with 6% iowait, and a fresh `mida build` 95% busy with none. A run that hits kept them 60 to 63% busy with 28 to 31% iowait. On the slower disk, with 74 misses among the hits, that was 46 to 52% busy with 42 to 49% iowait, and the plain build had 28 to 35% iowait too. iowait only counts a processor with nothing else to do, so the 6% is a floor.
- So `Fresh` is 1.25 on the EPYC 7763. Earlier runs gave 1.24, 0.99 and 1.22, and what those ran on is not recorded, so 0.99 is the odd one but not explained.
- A fresh `mida build` used about 30% more CPU time than the plain build beside it: 95% of four processors for 147 s against 88% for 117 to 123 s. Which part costs that is not measured.
- A run of hits waits on the disk. ext4 does not clone, so every hit is a plain copy, and a built checkout of this size is 12 to 14 GB: six fit in the 85 GB the runner has free and seven did not.

A job takes 14 to 18 minutes. A hosted runner's disk does not hold seven built checkouts of this size, so each is deleted once it is timed.

## Numbers

These tables predate the method above: one iteration, build servers left warm, and both edits made in one checkout after its hit.

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

Linux arm64 in a Docker container on macOS, overlay2, SDK 11 rc.3 through the `net11.0` host, 1024 projects, 2026-11-08. Overlay cannot clone, so every hit copies:

| scenario | 1024 |
|---|---|
| dotnet build, cold | 27.4 s |
| mida build, empty cache | 39.3 s |
| mida build, clean clone, all hits | 17.7 s |
| body edit of the widest leaf, 1 miss | 17.2 s |
| API edit of the widest leaf, 74 misses | 18.3 s |

Plain copies cost nothing visible here, because these outputs are small: the cache is 103 MB. That says nothing about a repo whose `bin/` folders hold gigabytes of package DLLs, which plan 4b item 4 still has to measure.

Findings:

- The first 1024 run had an all-hit run slower than a cold `dotnet build` (33 s against 27 s). The phases were restore 21.5 s, graph evaluation 6.4 s, prediction 0.9 s, graph build with 1024 hits 6 s. The in-process restore had no `MaxNodeCount`, so it ran on one node; with `ProcessorCount` it takes 11 s, the same as `dotnet restore`, and the table above is after that fix.
- MSBuild's floor on this repo is most of every row: a warm `dotnet build --no-restore` with nothing to compile takes 18.8 s, and compile is 2 to 3 s of the cold build. A cache can only remove compile, so here an all-hit run can at best be a few seconds under cold. Overhead per project: about 11 ms to evaluate, 6 ms per hit in the build including the key. If the benchmark should show a saving rather than a floor, give the generator a classes-per-project knob.
- A miss costs about 14 ms per project over a plain build at 1024 (43 s against 29 s). Storing is the likely cost.
- **74 misses among 950 hits took 62 s, slower than 1024 misses (43 s).** Fixed 2026-11-08: now 27.6 s, against 25.4 s for all hits. It was not superlinear work but two thread-pool starvations, found with `dotnet-dump` during the stall. In both, the CPU was idle and every pool thread waited on an MSBuild lock. Each hit's completion callback queues a pool item that takes that lock, so a burst of hits fills the pool, and the pool grows by about one thread per half second. Repeated runs swung between 30 s and 105 s.
  - `BuildManager.HandleResult` waits synchronously on the plugin's `HandleProjectFinishedAsync` while it holds the lock. The save awaited pool threads, which were queued behind that lock. The hook now captures target results and returns, the save runs in the background, and `EndBuildAsync` awaits every save. A project sharing a directory with another still saves inline, because a dependent may already write there. The hook's token is cancelled when the build ends, so background saves run without it.
  - MSBuild starts worker nodes inside the same lock, and on Unix each pipe handshake read completes on a pool thread. The first miss after many hits needed nodes, so node startup stalled. `mida build` now starts every node before the graph build, through a throwaway project that asks for one request per node. The nodes stay up for the session. An all-hit run pays node startup, about 1 s at 1024 projects. The project's temporary directory must outlive the nodes, because it is their working directory and evaluation fails once it is gone.
  - Rejected: `DOTNET_SYSTEM_NET_SOCKETS_INLINE_COMPLETIONS=1` fixed the stall but leaked to child processes, and the Roslyn compiler server deadlocked on it. Inline, a continuation did a synchronous read of its own pipe. A higher thread-pool minimum (`DOTNET_ThreadPool_ForceMinWorkerThreads` 128 to 1100) made runs slower, up to 156 s.

## Where the time goes, 2026-11-11

128 projects, macOS arm64 with 16 processors, SDK 10.0.404, Release. Phases timed inside the mida process, medians of five runs:

| phase | all hits | all misses |
|---|---|---|
| restore | 2.96 s | 2.84 s |
| graph evaluation | 0.96 s | 0.95 s |
| starting the worker nodes | 0.57 s | 0.55 s |
| graph build | 0.56 s | 4.67 s |
| the rest | 0.27 s | 0.28 s |
| total | 5.32 s | 9.29 s |

So in an all-hit build mida's own code is the 0.56 s of the graph build, and the rest is MSBuild and NuGet. A trace of the mida process (`dotnet-trace`, sampled thread time) splits its busy thread time into graph evaluation 38%, restoring hits 20% and starting nodes 15%. Half of the hits' share is `clonefile`. More than half of the evaluation is MSBuild's `FileUtilities.MaybeAdjustFilePath`, which asks the OS for the current directory on every relative value it checks: `getcwd` alone is 8 of the 17 s of evaluation thread time. That is MSBuild's code on macOS, and Linux was not traced.

Three changes came out of it, each measured against the one before in alternating runs:

| change | what it shortens | all hits | all misses |
|---|---|---|---|
| Restore with NuGet's static graph evaluation | restore, 2.9 to 2.2 s | 5.3 to 4.65 s | 9.3 to 8.6 s |
| No background garbage collector in mida | graph evaluation, 0.98 to 0.80 s | 4.42 to 4.35 s | 8.71 to 8.54 s |
| Worker nodes start while the graph is evaluated | the wait for nodes, 0.5 to 0.06 s | 4.54 to 4.22 s | 8.58 to 8.38 s |

- With the default restore every worker node evaluates projects, and mida has one cold node per processor, because it runs none in its own process. With static graph evaluation NuGet reads the projects in one process. NuGet calls the mode experimental and says a few repos restore differently with it, so `-p:RestoreUseStaticGraphEvaluation=false` turns it off. On the generated repo both modes gave the same keys.
- The nodes start on another thread once the build session begins, which is now before the graph exists. The plugin MSBuild takes at that moment is a `DeferredPlugin` that answers "not applicable" until the real one is set. The nodes are still all up before the first hit, which is what the stall fix above needs. Evaluation slows from 0.8 to 1.0 s beside the starting nodes, so the net is 0.3 s.

The benchmark at 128 projects, old build and new one back to back:

| scenario | mida build before | after | mida / dotnet before | after |
|---|---|---|---|---|
| Fresh | 9.5 s | 8.7 s | 1.43 | 1.31 |
| Unchanged | 5.1 s | 4.1 s | 0.78 | 0.61 |
| BodyEdit | 5.8 s | 4.9 s | 0.83 | 0.77 |
| ApiEdit | 7.5 s | 6.7 s | 1.16 | 1.04 |

Tried and dropped:

- Server GC shortens graph evaluation further, 0.98 to 0.70 s, but the process grows from 260 MB to about 600 MB.
- `TieredPGO` off and `TieredCompilation` off made no difference to the total.
- `ProjectInstance.TranslateEntireState`, which sends mida's evaluated project to the node in place of a second evaluation there, made no difference to a fresh build.

What is left: restore is half of an all-hit build, 2.2 of 4.2 s, and nothing caches it. Starting nodes is a fixed cost, so that change matters less at 1024 projects, and none of this was measured there or on Linux.

## How much of a hit is MSBuild, 2026-11-11

Status: measured, nothing built. 128 projects, SDK 10, Debug, mida in Release, a clean clone at another path, build servers shut down first. Two machines: macOS arm64 with 16 processors on APFS, and Linux arm64 in an OrbStack container pinned to 4 cores (`--cpuset-cpus 0-3`) on an ext4 loop image, SDK 10.0.402. Phases are timed inside mida, medians of three runs. Linux runs that stalled on the disk, see below, are left out.

All hits:

| phase | macOS, 16 processors | Linux, 4 cores |
|---|---|---|
| start: runtime, Locator, `dotnet --version` | 0.13 s | 0.08 s |
| restore: the node that runs the target | 0.24 s | 0.17 s |
| restore: NuGet's process evaluates the 128 projects | 1.45 s | 0.80 s |
| restore: the rest of NuGet's process | 0.45 s | 0.30 s |
| graph evaluation in mida | 0.9 s | 0.69 s |
| prediction | 0.1 s | 0.11 s |
| graph build: 128 keys, 5,979 files restored | 0.68 s | 0.18 s |
| total | 4.1 s | 2.4 s |
| plain `dotnet build` | 6.7 s | 7.2 s |

The other scenarios, total and the graph build in it:

| scenario | macOS | Linux |
|---|---|---|
| Fresh, 128 misses | 8.2 s, 4.8 s | 9.1 s, 6.8 s |
| BodyEdit, 1 miss | 4.8 s, 1.4 s | 3.1 to 3.4 s, 0.8 to 0.95 s |
| ApiEdit, 23 misses | 6.35 s, 2.9 s | 4.4 s, 2.2 s |

Restore, evaluation and prediction cost the same in every scenario.

- **MSBuild evaluates the 128 projects twice, in two processes.** NuGet's does it inside restore, and mida's does it for the graph. With prediction that is 2.45 of 4.1 s on macOS and 1.6 of 2.4 s on Linux. mida's own work on a hit, the keys and the restored files, is 0.68 s and 0.18 s.
- Restore is evaluation. On macOS a restore with nothing to do took 1.76 s, against 1.98 s on a clean clone. NuGet's evaluation costs more than mida's, and it also builds its collect targets in each project, which it reports as "128 builds".
- A second evaluation in one process is hardly cheaper than the first, 0.67 s against 0.77 s on macOS. So hosting NuGet's static graph restore inside mida would save two process starts, 0.3 to 0.4 s, and not the evaluation. Its class is internal, and its targets would run tasks in mida's process.
- Evaluation alone, as wall time and CPU time by processor count (`DOTNET_PROCESSOR_COUNT`):

  | processors | macOS | Linux |
  |---|---|---|
  | 1 | 1.90 s, 1.9 s | 1.27 s, 1.3 s |
  | 2 | 1.25 s, 2.3 s | 0.87 s, 1.5 s |
  | 4 | 0.77 s, 2.4 s | 0.48 s, 1.3 s |
  | 8 | 0.68 s, 3.7 s | |
  | 16 | 0.70 s, 6.9 s | |

  On macOS it gains nothing past 4 processors, and at 16 three quarters of its CPU time is in the kernel. So macOS numbers overstate what evaluation costs on a Linux runner.
- **MSBuild's scheduler is not what a hit waits for.** A prototype served every hit from mida's own walk of the graph, dependencies first, before the build session begins, and began no session when all hit. The walk took as long as the graph build had: 0.67 s against 0.68 s on macOS, 0.19 s against 0.19 to 0.31 s on Linux. What it saved is the worker nodes, which start beside the evaluation and are never used: 4.2 to 3.8 s on macOS, 2.45 to 2.3 s on Linux. The keys are the same: a cache filled through the plugin hit 128 of 128 through the walk, with `ProjectGraph.GetTargetLists` and each node's `GlobalProperties` in place of the request's.
- **What an evaluation asks can be recorded through public API.** `EvaluationContext.Create` takes an `MSBuildFileSystemBase`. Per project it saw about 57 existence checks, 14 of them distinct, and 3 enumerations inside the repo, plus the search for `Directory.Build.*` above it. `ProjectInstance.ImportPaths` lists the imports: 394 distinct files, 258 of them in the repo, which is two restore outputs per project and the two shared props. `EnvironmentVariableReadEventArgs` named 5 variables. Not checked: a property function that reads a file itself, such as `[System.IO.File]::ReadAllText`, should not pass through it.
- **An all-hit build writes 1.56 GB to restore 46 MB of blobs.** Each of the 28 test projects gets 55 MB of package files in `bin/`, among them TUnit's native coverage binaries for every platform. APFS clones them, ext4 copies them. With hard links in place of copies the graph build took 0.10 s, not 0.23 s, and the build 2.5 s, not 2.7 s (medians of ten, alternating). That was an experiment: a write through such a link would change the blob. A plain `dotnet build` writes the same bytes.
- On the ext4 image about one run in four lost about a second in a phase that writes. A thread pool of 64 did not change that, and mounting with `commit=60` made it rarer, so it is the disk and not mida. Whether a hosted runner does the same is unknown.

Where MSBuild could come out of a hit, with all-hit times that are sums of the phases above, not measurements, except the first:

| change | macOS | Linux |
|---|---|---|
| today | 4.1 s | 2.4 s |
| Hits before the session, no nodes when all hit (the prototype) | 3.8 s | 2.3 s |
| and no evaluation in mida: a stored graph, used when every recorded question still has the same answer | about 2.9 s | about 1.5 s |
| and no restore: its outputs come from the store when the same check passes and the packages are on disk | about 0.8 s | about 0.3 s |

The last row reverses the plan's "Restore: not cached". Restore outputs hold absolute paths, a floating package version would stay where the stored restore resolved it, and `dotnet test --no-build` has not run on such outputs. A miss still needs MSBuild, and with it an evaluation of at least the projects that are not yet decided.

## Real-world repos

Built and first run 2026-11-11: `tests/Mida.Tests/RealWorld.cs` (`[Explicit]`) and `.github/workflows/realworld.yml`, which runs only when dispatched. One test per repo, each a pinned tag:

| test | repo | tag | solution | projects | SDK its `global.json` selects |
|---|---|---|---|---|---|
| `OrchardCore` | OrchardCMS/OrchardCore, a runnable CMS | `v3.0.1` | `OrchardCore.slnx` | 227 | 10.0.200, `latestMajor`: the newest installed |
| `Abp` | abpframework/abp, a library | `10.7.1` | `framework/Volo.Abp.slnx` | 270 | 10.0.100, `latestFeature`: SDK 10 |

A test clones the tag once, restores there to fill the package cache, and then builds four clean clones of that clone, each at its own path:

1. `dotnet build` with the two properties `mida build` injects, `ContinuousIntegrationBuild=true` and `IncludeSourceRevisionInInformationalVersion=false`.
2. The same again at another path. A file that differs between 1 and 2 is not deterministic, so it is listed and not compared.
3. `mida build` with an empty cache.
4. `mida build` at a third path with the cache that 3 filled.

It hashes every file under a `bin` directory, with each mention of the checkout's own path replaced by a token. So a file that names its checkout compares equal across checkouts, and a file that a hit restored with the writer's path in it does not. The test fails unless 3 and 4 left the same files with the same hashes as 1. It writes `TestResults/realworld-<test>.md` with the times, the hit and miss counts and the first 50 differences, and all of them in the `.txt` beside it. The plain time is the faster of 1 and 2, and each mida time is a single run.

Run one: `dotnet test --project tests/Mida.Tests -f net11.0 --treenode-filter "/*/*/RealWorld/OrchardCore" --output Detailed`. The net11.0 build of mida loads either SDK, and the net10.0 build refuses OrchardCore on a machine that also has SDK 11.

First run, 2026-11-11, in an OrbStack container on four pinned cores, Ubuntu 24.04 arm64, ext4 loop image, SDK 10.0.402 and 11.0.100, mida's net11.0 build from the working tree. Both tests passed:

| repo | dotnet build | mida build, empty cache | mida / dotnet | mida build, filled cache | mida / dotnet | filled cache |
|---|---|---|---|---|---|---|
| Abp 10.7.1 | 51.9 s | 68.4 s | 1.32 | 60.0 s | 1.16 | 163 keys, 122 hits, 41 misses |
| OrchardCore v3.0.1 | 85.8 s | 91.9 s | 1.07 | 95.6 s | 1.11 | 0 keys |

- Abp: 56,397 files under `bin`, of which 450 differ between the two plain builds, by their names mostly test projects' executables and assemblies. Every other file is the same after both mida builds. The two plain builds took 75.2 s and 51.9 s, so one run each says little about the ratio.
- Abp's 41 misses in an unchanged clone are all "static web assets name this checkout": 33 of the 45 Web and Razor SDK projects and 8 projects that copy their manifests. All 163 keys are the same at both paths. Neither build said so, because that line went to MSBuild's logger as a message and the console logger is quiet. It is now printed as a `mida:` line.
- OrchardCore gets no keys: every project sets `TargetFrameworks`, with the one value `net10.0`, and a multi-targeted project is not cached. So this run compared three plain builds. Fixed 2026-11-12 ([cache-key.md](cache-key.md)), and the same test then gave 448 keys, two for each project: 88.5 s plain, 119.0 s with an empty cache (1.34), and 76.9 s with a filled one (0.87) on 339 hits and 109 misses. Which projects miss is not looked at yet.
- Abp again on 2026-11-12, once static web asset manifests were stored with tokens ([static-web-assets.md](static-web-assets.md)), same container on a 48 GB ext4 image (12 GB ran out): 49.2 s plain (the faster of 69.0 s and 49.2 s), 65.4 s with an empty cache (1.33), and 37.0 s with a filled one (0.75) on 163 hits and 0 misses, against 1.16 and 41 misses before. Both mida builds left the same files under `bin` as `dotnet build`, and neither said "not stored" or "different outputs". Cache 1,007 MB.
- Both on GitHub Actions on 2026-11-12, `ubuntu-latest` x64, four processors, after the static web asset change. Both passed, and both mida builds left the same files under `bin` as `dotnet build`. Abp: 127.0 s plain (the faster of 174.1 s and 127.0 s), 168.8 s empty (1.33), 101.5 s filled (0.80) on 163 hits and 0 misses. OrchardCore: 212.4 s plain (the faster of 218.0 s and 212.4 s), 208.1 s empty (0.98), 19.9 s filled (0.09) on 448 hits and 0 misses, so its 109 misses were this too.
- OrchardCore's own assemblies differ between two plain builds: 9,089 of 17,957 files, about 250 assemblies and their copies. The assembly holds the full path of its PDB, since `SourceRoot` has the package folder and not the repo, so `PathMap` maps nothing in the checkout. The repo references `Microsoft.SourceLink.GitHub` 10.0.300 as a `GlobalPackageReference` and built on SDK 11. Which of the two leaves the repo's root out is not checked.

Not covered: `obj`, starting the app, and edits. The plan for edits stands: the scenarios 0 to 3, with one generic partial edit: append a comment line to a `.cs` in a leaf project. That changes the key but not the reference assembly, so it checks early cutoff without knowing the repo. API edits need repo knowledge, so skip them there.

Two filters: the repo must build with SDK 10 or 11 (`global.json`), and Arcade repos (aspire, runtime, roslyn, efcore, aspnetcore) bootstrap their own SDK through `eng/build.sh`, so they cost weeks, not an afternoon. `dotnet/runtime` is out for good: native code, custom build. Aspire is possible later with its pinned SDK. Plain-SDK repos of real size to try first, in order: the 49-project repo the plan already names, OrchardCore, semantic-kernel `dotnet/`, OpenTelemetry .NET, Akka.NET. Their SDK pins and project counts are unchecked.
