# `mida test`: run only the tests that have not passed

Status: built 2026-11-17, first cut. Milestone 7 of [plan.md](../plan.md). Microsoft.Testing.Platform only: VSTest was dropped the same day, when the [plan's scope](../plan.md#scope) became the newest tools only.

## What it does

`mida test [build options] [--all] [--include <glob>] [--exclude <glob>] [--category <name>] [-- <runner arguments>]` builds as `mida build` does. If the build succeeds, it runs each test project whose pass is not in the store, and stores each pass.

- **Test node.** A graph node that is a test project and not an outer build. A test project with two frameworks is two test nodes.
- **Test key.** The environment lines of a build key, one line per node of the test node's transitive closure, and the runner arguments in order. A node's line is the hash of what a build key holds of that node alone: `project`, `target`, `property`, `input`, `missing` and `packages` (`KeyComputer.ComputeTest`). No reference assemblies, because a body edit two levels down must run the test. No outputs, so a multi-targeted dependency without a build key still counts.
- **Record.** `v1/test/<key>` holds how long the pass took. It is written when the runner exits with 0, and never with `--read-only`. A failure stores nothing, so it runs again.
- **Run.** Every test key is computed before the first test starts. A test node whose pass is stored is skipped. `--all` runs every node and still stores passes.
- **Choice**, since 2026-11-20. `--include` and `--exclude` are globs over a test project's path from the repo root, through `FileSystemName.MatchesSimpleExpression`, where `*` matches `/` too. `--category` matches a name in the project's `MidaTestCategory`, split on `;`, ignoring case. A test node runs when it matches an `--include` or there is none, matches no `--exclude`, and names a `--category` or there is none. None of the three is in the test key, so a pass stored under one choice counts under any other. A choice of no test node ends the run with 1 before anything is built, so a typo cannot read as green.
- **Building only what the chosen need.** With any of the three, the walk and the build cover the closure of the chosen test projects: all their nodes, outer and inner, and every node they reference. MSBuild gets a smaller `ProjectGraph` whose entry points are the solution's entry points in the closure and whose nodes are the `ProjectInstance`s the solution graph evaluated, so each node has the global properties and target list it has in a whole build, and the same key. `GeneratedRepoTests` checks both ways: a filtered run's entries hit in a whole run, and a whole run's in a filtered one. A node the solution graph does not have is evaluated again with a warning. Restore and the evaluation still cover the whole solution when no stored graph fits.
- **Opting in and out.** `MidaStoreTestPasses` false in a project runs it every time and stores no pass of it. False in `Directory.Build.props` and true in some projects stores only theirs. Both properties are in the stored graph, whose objects moved to `v2/graph` for them.
- **Result.** The whole output of each test node that failed, one line per test node that ran, then `3 test projects run, 1 failed, 5 passed before, 12.4 s of test time saved`. The exit code is that of the first test node in the graph that failed, else 0. After a failed build no test runs.
- **Failures of the cache.** A key that cannot be computed, or a store that fails, runs the test with a warning.
- **A test that cannot run** counts as failed: a missing assembly, or a runner that refuses its arguments.

## Spike: how to run a test project

Run 2026-11-17 on macOS arm64 with SDK 10.0.404 and 11.0.100, which behaved the same. TUnit 1.81.5 stood for Microsoft.Testing.Platform. For VSTest a throwaway MSTest project in a scratch directory was used, since TUnit has no VSTest mode.

What marks a test project, as evaluated properties:

| project | `IsTestProject` | `IsTestingPlatformApplication` | `RunCommand` |
|---|---|---|---|
| library | empty | empty | empty |
| TUnit | `true` | `true` | the app host |
| MSTest on VSTest | `true` | `false` | `dotnet` |

Ways to run without a build:

| command | result |
|---|---|
| `dotnet test --project <project> --no-build`, Microsoft.Testing.Platform mode | Works, 1 to 2 s for one test, because it evaluates the project. `--project` takes one project only |
| `dotnet test --test-modules "<dll>;<dll>"`, the same mode | Works with no evaluation, about 1 s. One exit code for all modules |
| `dotnet <dll>` or the app host, a Microsoft.Testing.Platform project | Works, 0.12 s |
| `dotnet test <project> --no-build`, VSTest mode | Works |
| `dotnet vstest <dll>`, a VSTest project | Works in both modes |
| `dotnet vstest <dll>`, a Microsoft.Testing.Platform project | Aborts, exit code 1 |

Exit codes:

| runner | pass | a test failed | no test matched the filter |
|---|---|---|---|
| Microsoft.Testing.Platform | 0 | 2 | 8 |
| VSTest | 0 | 1 | 0 |

`dotnet test --project` starts the test application in the caller's directory. Compared with a direct start it adds its own pipe arguments (`--server dotnettestcli --dotnet-test-pipe`), `DOTNET_ROOT_<ARCH>` and a few variables of its own.

## Decisions

- **mida starts each test node itself.** `dotnet exec <TargetPath>`, with the SDK's own `dotnet`, in the current directory. That needs no MSBuild, so an unchanged clean clone still never loads it. It also gives one exit code per node and works whatever runner `global.json` names.
- **Microsoft.Testing.Platform only.** A VSTest test project counts as failed with a message, rather than being skipped, which would read as covered. Its framework's Microsoft.Testing.Platform mode is the fix. `SnapshotProject.Test` is `mtp`, `vstest` or null, so the refusal can name the project.
- **As many nodes at once as the machine has processors**, which is `dotnet test`'s default too. The first cut ran one at a time, and a fresh run of 28 test projects then took 3.34 times as long as `dotnet test`. A node's output is held until it ends and printed only when it failed, so two nodes' lines never mix. There is no option for the number yet.
- **Zero tests is not a pass**: exit code 8 is a failure, as with `dotnet test`.

## Not the same as `dotnet test`

- `RunArguments`, `TestingPlatformCommandLineArguments`, a `.runsettings` file named in the project and launch profiles are not applied. Pass what is needed after `--`.
- What follows `--` reaches the test application's own command line, as with `dotnet run`. For xUnit v3 that is xUnit's native one unless the project sets `UseMicrosoftTestingPlatformRunner`: its generated `Main` starts Microsoft.Testing.Platform only when given `--server`, as `dotnet test` gives it. Seen 2026-11-17 with `xunit.v3.mtp-v2` 3.2.2: the project says `IsTestingPlatformApplication`, passes with 0, fails with 1, and `--filter-method` is "unknown option". Pass and failure are told apart either way. A filter that matches nothing may exit with 0 there, and that pass is stored under those arguments.
- A self-contained test application has no `dotnet exec`. Not tried.
- There is no combined report, and a passing test node's own output is not shown. Ask the runner for a report file after `--`.

## Known unsound

- A test that reads the clock, the network, a database, an environment variable or a file that is not a build input passes from the store when that changed. `--all` runs everything, and `MidaStoreTestPasses` false runs one project every time.
- A target that only a project outside the chosen closure asks of a node, such as a web app's static web asset targets of a library, is not run in a filtered build, so the node has another key there. The walk looks with the solution's target list, and the plugin looks again with the list MSBuild asks for. So such a node misses once in filtered runs and once in whole ones, and then hits in each. Before that second look, the miss was stored under the walk's key, and a whole build hit an entry without the target: `GeneratedRepoTests.FilteredRunHitsWhereOnlyAProjectOutsideItAddsTargets` reproduces that without the fix.
- The .NET runtime's patch version is not in the key. The SDK version is. The machine is named only by its runtime identifier, so a pass on one Linux distribution or macOS version stands on another.
- Test keys are hashed after the build. A source edited while the build runs can pass on what was built before the edit, and that pass is stored under the edited source. Found by the 2026-11-17 review for a multi-targeted dependency, whose inputs nothing hashed earlier. Not a case for CI, where nothing edits the checkout.
- Two test nodes with one `TargetPath` both run whichever assembly was built last, so neither pass is stored.
- A runner argument that runs no test but exits with 0, such as `--help` or `--list-tests`, stores a pass under those arguments. Other arguments are another key.
- A solution with no test project ends `mida test` with 0, and says `0 test projects run`.
- A test project that hangs shows nothing until it ends, and nothing ends it. Pass the runner a timeout after `--`, such as Microsoft.Testing.Platform's `--timeout`.
- A `--` inside a response file reaches mida's own parser, so runner arguments cannot come from one.
- A stored graph is trusted for the test marker as for everything else.

## Checks

TUnit, `GeneratedRepoTests`, on net10.0 and net11.0, with the generated graph as the oracle:

- An unchanged clean clone at another path runs no test project.
- After a body edit, exactly the test projects above the edited library run and fail, with exit code 2, and they run again on the next run.
- After an API edit the same test projects run and pass, and the next run runs none.
- A read-only run stores no result. Other runner arguments run everything again. `--all` runs everything. A runner argument the runner refuses fails every test project.

`CacheEndToEndTests`, with the emulator: a pass stored by one machine spares a second machine the test, though its local store starts empty.

By hand, in a scratch directory, with the real `mida test`:

- A TUnit project on `net10.0;net11.0`: two test nodes, each ran, passed, was stored, and failed on demand with exit code 2.

The refusal of a VSTest project has no automated test: fixtures are TUnit only, and TUnit has no VSTest mode.

## Later

Build only what the tests to run need also without a choice, where today the whole solution is built: then a project no test needs is skipped too. Restore and evaluate only the chosen closure. An option for how many test nodes run at once. Shard. Select single tests.
