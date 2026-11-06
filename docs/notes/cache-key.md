# Cache key

Status: implemented, 2026-11-06. Answers [plan milestone 3](../plan.md#3-key-computation). Code: `src/Mida/KeyComputer.cs`. Checked on macOS arm64 against a small repo in `tests/Mida.Tests` and a private monorepo of 49 projects under SDK 10.

## What is in a key

A key is the SHA-256 of sorted text lines. `mida build --key-details <dir>` writes those lines per project, so a false miss can be diagnosed by diffing two runs.

| Line | Source | Why |
|---|---|---|
| `sdk`, `msbuild` | The loaded SDK | SDK files are not hashed, so their version stands for them |
| `platform` | Runtime identifier | Outputs include a native apphost |
| `project`, `target` | The build request | Stored target results only answer the targets requested |
| `property` | Global properties, minus the ignore list | `{RepoRoot}` replaces the checkout path |
| `input` | Predicted inputs in the repo: relative path and content hash | Sources, project files, imported props and targets |
| `missing` | Predicted inputs that do not exist | A file appearing must change the key |
| `packages` | Hash of four sections of `project.assets.json` | What was resolved, without the absolute paths in the rest of the file |
| `dependency-ref` | Hash of each transitive dependency's reference assembly | Early cutoff: a body edit stops at its project |
| `dependency-key` | The key of a dependency that runs at build time | See below |

Not read: anything under the dotnet root, the NuGet root, or any project's `BaseIntermediateOutputPath` or `BaseOutputPath`. The SDK and packages are covered by version, and outputs are not inputs.

The key must be computed during the build. Reference assemblies do not exist in a clean checkout until dependencies build or materialize, so `GetCacheResultAsync` is the place. MSBuild asks about a project only after its dependencies finish: 0 violations across 163 dependency edges on the real repo.

## Additions to the plan's key

- **Platform.** A Linux apphost must not hit on macOS. The cost: a macOS laptop gets no hits from Linux CI, which limits read-only laptops to machines matching CI.
- **Ignored properties.** `CurrentSolutionConfigurationContents` lists every project, so without the ignore list any project joining the solution would invalidate the repo. The other `Solution*` properties name the entry point, not the build. A project that builds differently through `$(SolutionDir)` is covered only by the path staying the same.
- **Build-time dependencies.** A `ProjectReference` with `OutputItemType` set, or `ReferenceOutputAssembly=false`, is typically a source generator. It runs during compilation, so a body edit can change the dependent's output with no API change. Those edges use the dependency's full key.
- **Packages from restore's result.** A floating version can resolve higher with no repo file changing, so hashing `Directory.Packages.props` is not enough. Only `version`, `targets`, `libraries` and `projectFileDependencyGroups` are read.

Missing keys are safe: if a key cannot be computed, a warning is logged and the project builds normally. A multi-targeted project has several graph nodes, gets no key, and builds normally.

## Results

Real repo, clean checkouts:

| Check | Result |
|---|---|
| Different path, next commit | 49 of 49 keys identical |
| Rebuilt in place | 49 of 49 stable |
| Method body edit in a shared library | 1 key changes |
| Public type added there | 14 keys change, an exact match with the project graph |
| Keys not computable | 0 |

`tests/Mida.Tests/KeyEndToEndTests.cs` runs real builds for each milestone check: different paths, source, package and `Directory.Build.props` edits, and an SDK change, each asserting exactly which keys change. It also covers solution membership, build-time dependencies and floating versions.

Each key rule was removed in turn to confirm a test fails. Six of seven did. Removing the `sdk` line survives because the test also changes the `msbuild` line and `global.json`. The rule stays: a machine rolling forward to a newer SDK band changes neither of those for certain. The transitive-reference rule is only caught through build-time dependencies, because the static graph already gives a project edges to its transitive references.

## Cost

Release build, 49 projects: 0.38 to 0.43 s, about 8 ms per project, against a target of 10 ms. That is CPU time summed across up to 12 threads, so elapsed time is lower.

| Stage | Summed time |
|---|---|
| Inputs, mostly hashing 91 MB in 3,011 files | About 250 ms |
| `project.assets.json` | About 120 ms |
| Dependency references | About 10 ms |

No on-disk hash cache is needed: every run is already under target, and on CI every run is a first run anyway.

Parsing `project.assets.json` with `JsonDocument` cost 340 to 520 ms under build concurrency, against 50 ms alone. `Utf8JsonReader` hashes the wanted sections in place. Inside the build, prefer no allocation over simple parsing.

Prediction itself took 39 ms for 49 projects.

## Not tested

- Linux and Windows, including case sensitivity and `\` in property values.
- Environment variables. They are not in the key, so one that changes build output produces a false hit. The extra-inputs setting in the plan's risks would cover this.
- Projects outside the 49: the spike repo has no real source generators, and the build-time rule was tested with a plain library.
- Prediction accuracy. Nothing here finds undeclared inputs, which is the plan's first known risk.
