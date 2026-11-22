# Stored graph

Status: built 2026-11-12, from the measurements of 2026-11-11 in [benchmarks.md](benchmarks.md#how-much-of-a-hit-is-msbuild-2026-11-11). This note was written 2026-11-16 from the code. Code: `GraphStore.cs`, `GraphSnapshot.cs`, `Questions.cs`, `Checkout.cs`, `PathTokens.cs`, `RecordingFileSystem`, `EnvironmentLogger` and `Ask` in `GraphEvaluation.cs`, `ProjectCache.WalkAsync`, `BuildRunner.Run`. Changes one decision of the [plan](../plan.md): restore was "not cached".

## Why

An all-hit build was mostly MSBuild and NuGet. At 128 projects restore and evaluation took 2.45 of 4.1 s on macOS and 1.6 of 2.4 s on Linux, and mida's own work on the hits 0.68 s and 0.18 s. MSBuild evaluated every project twice, once in NuGet's process and once in mida's.

## What a run does

1. **Find a graph.** The first key is a hash of mida's own build (its module version id), the SDK version, the platform, the solution's relative path and content, and the global properties with roots as tokens. A graph is stored under `v1/graph/<key>`, as gzipped JSON.
2. **Check that it fits.** A graph holds the questions its evaluation asked of the machine, with the answers. It stands for an evaluation of this checkout while every answer is the same: file contents and environment variables first, then what exists and what directories list, once the restore outputs are in place.
3. **Or try the next one.** When an answer differs, the next key is a hash of this key and this checkout's answers to that graph's questions. So the graphs of one solution form a chain, and a checkout walks it for up to 8 graphs.
4. **Skip restore.** The graph holds `project.assets.json` and the generated `.nuget.g.props` and `.nuget.g.targets` of each project as blobs with tokens for the roots. They are written with this machine's paths when the stored restore can be used again, no root needs escaping, and every file NuGet expects in the package folder is there. Otherwise restore runs, and the graph still fits when restore generated the same props and targets. The assets file is not compared, because a key reads the one on disk.
5. **Serve the hits before MSBuild.** `ProjectCache.WalkAsync` goes through the graph, dependencies first, and restores every hit whose dependencies all hit. When that is every project, the run ends without loading MSBuild. Otherwise a build session starts and the plugin answers from the same `ProjectCache`.
6. **No graph fits.** Restore, evaluate, and store the graph under the first free key, beside the build. Not with `--read-only`, and not with `--evaluate`, which also skips the search.

The first line of the summary says which it was: `Graph from the store, restore skipped`, `Graph from the store`, or `Graph evaluated: <what> changed`, with `Not stored: <why>` when it was not.

## What is recorded

- MSBuild's evaluation asks the disk through `RecordingFileSystem`, an `MSBuildFileSystemBase`: whether a path exists, what a directory lists.
- `EnvironmentLogger` hears which environment variables an evaluation read. A property an evaluation read with no variable of its name is recorded as unset, so setting it later evaluates again.
- `Ask` adds what MSBuild's file system never hears: the files it imports, and what the SDK and the predictors ask the disk directly.
- The SDK's own files are not recorded. Its version is in the key, as for a project's key.
- A listing leaves out NuGet's bookkeeping (`project.nuget.cache`, `*.nuget.dgspec.json`) and mida's `*.mida-restored.txt`.
- A file found by searching upwards, such as `Directory.Build.props`, is named by how far above the repo it is. A machine with more directories above its checkout is asked about those too.

## When it is not used

Nothing is stored when a project file reads the machine in a way no question records, such as a property function that reads a file, or when one question got two answers.

Restore is never skipped when:

- a target of the repo runs in restore, since what it reads is in no question,
- a file reads a property that only restore sets,
- a version floats: its range holds a `*`, and it resolves again on every restore,
- restore writes outside the repo,
- a root holds a character that XML, JSON or MSBuild escapes,
- a package file or a blob is missing.

A NuGet config is never read: it can hold credentials, and a CI job writes a new token into one on every run.

## Numbers

- Generated repo, 1024 projects, `Unchanged`, on GitHub's hosted runners with an AMD EPYC 7763: 0.71 of a plain build on 2026-11-10, and 0.28 on 2026-11-14.
- A web CMS of 227 projects, 448 keys, all hits, on a hosted runner: 16.8 s against 162.9 s, 0.10.
- An application framework of 270 projects, in the OrbStack container: the graph is found and checked and restore skipped in 1.0 s. Its 107 multi-targeted projects have no key, so MSBuild still loads and evaluates for 6.0 s ([multi-targeting.md](multi-targeting.md)).

## Checks

`StoredGraphTests`: a clean clone at another path needs neither restore nor MSBuild, keys from a stored graph equal those of an evaluation, each change an evaluation would see evaluates again, a source edit keeps the graph, and one test for each case in the list above. `QuestionsTests` and `PathTokensTests` cover the answers and the tokens, `GraphEvaluationTests` what an evaluation records.

## Known limits

- A checkout that has been built answers differently from a clean one, since `bin/` is there. It gets a graph of its own, which costs one restore and one evaluation per clone: 20 s and 8 s on the application framework.
- A new build of mida starts a new chain.
- Of two environment variables that differ only in case, the first listed is the one asked.
- Environment variables an evaluation read are questions of the graph, but not lines of a project's key.
- Not run on Windows.
