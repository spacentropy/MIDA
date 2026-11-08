# Local store

Status: implemented, 2026-11-07. Answers [plan milestone 4](../plan.md#4-local-store-end-to-end). Code: `src/Mida.Store`, `src/Mida/MidaCachePlugin.cs`. Checked by real builds and real `dotnet test --no-build` runs of a five-project repo, on macOS arm64 and Linux arm64 in a container, on SDK 10 and 11. **Not yet run on the 49-project repo.**

## What is stored

```
<cache>/v1/cas/<sha256[0:2]>/<sha256>    one blob per file, uncompressed so it can be cloned
<cache>/v1/ac/<key>                      manifest, JSON
```

A manifest holds `files` (path, hash, Unix mode), `references` (path, source) and `targets` (replayed items). Paths are relative to the repo root. Item values use `{RepoRoot}/`. Blobs are written before the manifest, each to a temporary name and then renamed.

Two more files are left out of the manifest beyond [relocation.md](relocation.md): `*.CoreCompileInputs.cache` and `*.GeneratedMSBuildEditorConfig.editorconfig`. They were the only ones that differ between checkouts of one commit. Without them, manifests are byte-identical across paths, so in milestone 5 a second writer's different manifest really means nondeterminism.

That needs a commit. In a `git init` with no commits there is no source root, so `PathMap` is not set and DLLs differ by path. Hits still work, and CI always has a commit.

## Decisions

**`IObjectStore` takes paths, not streams.** `File.Copy` already clones: 2 GB took 0 ms on APFS. So copy-on-write needs no P/Invoke as long as the store sees paths. The interface had four methods, two for files and two for bytes. Since milestone 5 it has three: a save passes bytes, so the stored bytes are the ones hashed (see the plan's milestone 5), and only a restore takes a path. Swap, list and delete from [the note](build-cache-object-store.md) wait for refs and GC.

**A blob is verified once per run.** Hashing every copy would cost more than the clones, and the same DLL lands in many `bin/` folders. The first file restored from each blob is hashed, and a mismatch deletes that file and misses.

**No existence check before restoring.** A failure partway leaves some restored files behind. That is safe because the miss clears them first.

**A project's outputs end up matching its entry.** A hit deletes every cached-kind file in the project's output directories that the manifest does not list, and a miss deletes all of them before building. A hit skips MSBuild's incremental clean, and a later miss cannot run it either, because `*.FileListAbsolute.txt` is not cached. Without this, a file of another revision stayed in `bin/` on a hit, changed dependents' `dependency-files` line, and on the next miss was stored under the new key, where a create-only manifest keeps it for good. Files of the excluded kinds stay. The cost is that a failed build leaves that project's outputs empty.

Both deletes stay inside the project's `bin/` and `obj/` (`BaseOutputPath`, `BaseIntermediateOutputPath`), which the build owns. An `OutDir` elsewhere can hold the repo's own files: a committed `out/README.md` was deleted by the first miss before this rule. Neither delete runs when another project writes under the same directory, because a scan cannot tell whose file it sees.

**A project with static web assets is not stored.** Their manifests name content roots absolutely: `*.staticwebassets.runtime.json` in `bin/`, and `staticwebassets.build.json` and others in `obj/`. An app reads the first at startup, in Development, which is also what `WebApplicationFactory` uses. Relocated, the app crashed with `DirectoryNotFoundException` once the writer's checkout was gone, and until then it served the writer's files. So a project whose `*staticwebassets*.json` outputs contain the repo root always misses, and that includes its copies in dependents' `bin/`. A Razor class library, its host and a test project of the host were all checked. On SDK 10 this catches only projects with assets. On SDK 11 every Web SDK project has framework assets, so it catches all of them. Give these files a `{RepoRoot}` token if web projects' misses cost too much.

**A reference needs the same relative path and the same bytes.** Bytes alone would tie unrelated files together, such as two empty ones. Only `OutDir` is matched, never `obj/`. On a hit the source is copied from the dependency's current output, whether that dependency hit or missed.

**Failures are a miss with a warning**, which covers a missing or corrupt blob, a bad manifest and any I/O error. An exception that escapes a hook fails the build, so `Parallel.ForEach`, which wraps failures in `AggregateException`, must not be used here. `Parallel.ForEachAsync` rethrows the original.

**Manifest paths are checked against the repo root**, and store keys against the store root, because the store is not trusted. This covers both a written path and a blob hash used as a path.

**A manifest is validated before the first file moves.** JSON options reject null or missing properties, and `Validate` covers what they cannot see: entries inside collections, hash shape, mode range, empty or repeated target names, and every path. What only MSBuild can judge, such as an empty item spec or a reserved metadata name, throws `ArgumentException`, which is also a miss. A result holding one target twice hangs MSBuild instead of failing, and an unhandled hook exception fails the build with no message at all. A well-formed manifest that lists the wrong targets or hashes is out of scope, since nothing signs a manifest.

## Which stale outputs each mechanism stops

| Change in a dependency | Its reference assembly | Stopped by |
|---|---|---|
| Method body | Same | Reference, on every hit |
| Edit to a file copied to output | Same | Key: the predictor makes it an input of dependents |
| New file copied to output, or project file edit | Same | Key, likewise |
| New satellite assembly from a new `.resx` | Same | Key: the `dependency-files` line, see [cache-key.md](cache-key.md) |

So copied files never reach the reference path in practice: a dependent misses first. The spike expected them to rely on references.

## Checks

Mutations, repeated after the suite moved to TUnit: turning off references fails exactly the two body-edit tests, removing the `dependency-files` line fails exactly the satellite test, and going back to `Parallel.ForEach` for references fails the two manifest tests that reach it. The copied-file cases stay green with references off, because the key already covers them.

Mutations for the review fixes, each failing only its own tests: no delete on a hit, no delete on a miss, no shared-directory guard, deletes outside `bin/` and `obj/`, a dependency's file stored as its own source, no static web asset check, no `Validate`, no JSON options, no NUL check, duplicate targets allowed, `ArgumentException` not a failure.

A hit on a two-project repo took about 0.6 s, restore included, against 0.8 s cold. That compile is too small to say anything about saving.

## Known limits

- `File.Move(overwrite: false)` checks and then renames, so racing local writers can both report created. The object stays whole. GCS preconditions will be strict.
- A corrupt local blob stays, so its key misses with a warning until the cache is deleted. A store has no delete yet.
- A manifest whose blob is gone keeps its key missing unless a rebuild produces the same blob, because manifests are create-only. Deterministic builds do.
- Nothing evicts `~/.mida/cache`.
- Unrelated projects sharing one `OutDir` are not supported. Each stores the other's files, so concurrent restores collide, which is a miss with a warning. Worse, the last copy wins, so one can restore the other's older bytes without warning. And a file rewritten between being hashed and stored puts the wrong bytes in a blob, which then misses as corrupt for good. A dependency and its dependent in one directory are safe, but the dependent misses once more on the run after its first, because the directory lists its own outputs as the dependency's. Obsolete files there are never deleted.
- An `OutDir` outside `bin/` and `obj/` keeps obsolete files.
- A checkout path containing `'` fails the build with `CS8101`: `ContinuousIntegrationBuild` turns the path into a `PathMap` that csc cannot parse.
- A project with static web assets always misses, and so does anything that copies its manifests.

## Not tested

- The 49-project repo: restore time, store overhead on a miss, and the plan's 3 s hit.
- Whether `File.Copy` reflinks on Linux. No btrfs or XFS volume was tried, and the container's overlay filesystem cannot clone.
- Windows, Release builds, different NuGet roots. Clearing the read-only attribute before a delete is Windows-only code that no run has exercised.
