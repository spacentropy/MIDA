# MIDA - .NET Monorepo Multi Tool

`mida build` builds a .NET repo through MSBuild's static graph with an output cache on local disk and Google Cloud Storage. Each project gets a key from its inputs. On a hit its outputs are restored instead of built, at any checkout path, so a clean CI clone of an unchanged repo restores and builds nothing. `mida test` then runs only the test projects that have not passed with their current inputs. It is a CI tool: the target is CI build and test time.

Status: an MVP, not released yet. On Linux x64 a clean clone with a filled cache builds OrchardCore's 227 projects in 0.10 of a plain build's time, and Abp's 270 in 0.82, since its multi-targeted projects are not cached. Windows has never been run. See [docs/plan.md](docs/plan.md) for what is proven, what is next and the known risks.

## Install

There is no package on nuget.org yet. Until the first release, pack it from a clone:

```sh
dotnet pack src/Mida -c Release -p:Version=0.0.0-dev -o out
dotnet tool install -g mida --version 0.0.0-dev --add-source out
```

It needs .NET SDK 10 or 11, and it loads MSBuild from the SDK your `global.json` selects. mida must run on that SDK's runtime, because a runtime cannot load a newer SDK's MSBuild. If it cannot, it stops with an error.

## Use

```sh
mida build [<solution>] [-c <configuration>] [-p:<name>=<value>] [--cache <dir>] [--cache gs://<bucket>[/<prefix>]] [--read-only] [--evaluate]
```

- `<solution>` defaults to the only `.sln` or `.slnx` in the current directory.
- `-p` takes one property each. Unlike `dotnet build`, it does not split on `;`.
- `--cache <dir>` is the local store, `~/.mida/cache` by default. Nothing evicts it.
- `--cache gs://...` adds a GCS store behind the local one. Hits restore from local disk, and what came from the bucket is kept locally.
- `--read-only` stores nothing in either store.
- `--evaluate` restores and evaluates every project, even when the store holds a graph that fits.

It first looks in the store for an evaluated graph of the solution. When one fits the checkout and the packages are on disk, it skips restore and evaluation, and when every project then hits, it never loads MSBuild. Otherwise it restores, then builds. It ends with a summary such as `47 hits, 2 misses, 312.4 s of project build time saved`. The time saved is the stored build time of each hit, summed over projects, so it is not wall-clock time. A key that cannot be computed and any store failure are misses with a warning: the cache never fails a build.

`mida build` always sets `ContinuousIntegrationBuild=true` and `IncludeSourceRevisionInInformationalVersion=false`, which make outputs the same at every checkout path and keep the commit SHA out of reference assemblies. A `-p` for either wins, at the cost of hits.

It restores with NuGet's static graph evaluation, which is faster and which NuGet calls experimental. `-p:RestoreUseStaticGraphEvaluation=false` restores the default way.

## Test

```sh
mida test [<solution>] [the options of mida build] [--all] [-- <runner arguments>]
```

It builds as `mida build` does, then runs each test project that has not passed before with the same sources, packages, properties, SDK and runner arguments, in the project itself and in everything it references. A pass is stored, a failure is not. It ends with a summary such as `3 test projects run, 1 failed, 5 passed before, 12.4 s of test time saved`, and exits with the code of the first test project that failed.

- What follows `--` goes to every test project's runner, such as `-- --report-trx`.
- `--all` runs every test project, also one that passed before.
- `--read-only` stores no pass either.
- Test projects must use Microsoft.Testing.Platform, as TUnit does and as xUnit v3, NUnit and MSTest can. A VSTest project counts as failed. Each runs as its own application, as many at once as the machine has processors. Only a failed test project's output is shown. `RunArguments`, a `.runsettings` file named in the project and launch profiles are not applied.

A test that depends on something outside its inputs, such as the clock, the network, a database or an environment variable, is not run again when only that changed. Run those with `--all`. See [docs/notes/mida-test.md](docs/notes/mida-test.md).

## CI

- Run every later step with `--no-build` and the same configuration, such as `dotnet test -c Release --no-build`. A step without it rebuilds everything. `mida test` builds and tests in one step.
- Packages are not cached. Cache `~/.nuget/packages` with your CI's own cache: restore is skipped only when they are all on disk.
- Give PR runners `--read-only`, so only trusted builds write to the bucket.
- Credentials come from Application Default Credentials: `GOOGLE_APPLICATION_CREDENTIALS`, gcloud's, or the metadata server. From GitHub Actions, use Workload Identity Federation through `google-github-actions/auth`. Writers need `roles/storage.objectCreator` and `roles/storage.objectViewer` on the bucket, read-only runners only the viewer.
- Give the bucket one age-based lifecycle rule. mida never deletes, and a rule that removes manifests along with their blobs keeps every entry whole. Add `"matchesPrefix": ["<prefix>/"]` to the condition to cover only mida's prefix.

  ```sh
  echo '{"rule":[{"action":{"type":"Delete"},"condition":{"age":30}}]}' > lifecycle.json
  gcloud storage buckets update gs://<bucket> --lifecycle-file=lifecycle.json
  ```

A GitHub Actions job:

```yaml
permissions:
  contents: read
  id-token: write
steps:
  - uses: actions/checkout@v4
  - uses: actions/setup-dotnet@v4
    with:
      global-json-file: global.json
  - uses: actions/cache@v4
    with:
      path: ~/.nuget/packages
      key: nuget-${{ runner.os }}-${{ hashFiles('**/*.csproj', '**/Directory.Packages.props') }}
  - uses: google-github-actions/auth@v2
    with:
      workload_identity_provider: projects/<number>/locations/global/workloadIdentityPools/<pool>/providers/<provider>
      service_account: <name>@<project>.iam.gserviceaccount.com
  - run: dotnet tool install -g mida
  - run: mida build -c Release --cache gs://<bucket>/mida ${{ github.event_name == 'pull_request' && '--read-only' || '' }}
  - run: dotnet test -c Release --no-build
```

To run only the tests that have not passed, replace the last two steps with `mida test` and the same options.

## Limits

- Projects that target several frameworks are not cached. They are built on every run and are in neither the hits nor the misses: a line such as `3 nodes without a key: 3 multi-targeted` counts them, one node per framework and one for the project. Their dependents are cached, and so is a project with one framework in `TargetFrameworks`.
- A project that sets `GeneratePackageOnBuild` is not cached either, since a hit would not write its package. The same line counts it.
- Nor, for now, is a project that copies to its output a file it writes into its own `bin/` or `obj/` outside the directories a hit restores, such as `obj/Debug/Scripts/`: `copies a file it writes outside its outputs`. See [docs/notes/relocation.md](docs/notes/relocation.md).
- A Web or Razor SDK project with a static web asset outside the repo, the package folder, the .NET install and the user profile is not cached, and the build says why. Static web assets on Windows have not been tested.
- Environment variables are not in the key.
- In a repo on Microsoft's Arcade SDK, such as dotnet/aspire, set `NUGET_PACKAGES`. Under `ContinuousIntegrationBuild`, which mida sets, Arcade otherwise moves the package folder into the checkout.
- Tools that stamp a version from git, such as MinVer, NBGV and GitVersion, cost the hits of every dependent whenever they change `AssemblyVersion`: MinVer on a major version, NBGV on a minor version and GitVersion on a patch by default. A hit can serve outputs stamped with an older version, which matters at pack and publish.

## Develop

```sh
dotnet test --project tests/Mida.Tests -f net10.0
dotnet test --project tests/Mida.Tests -f net11.0
```

Each run takes about 13 minutes and starts many MSBuild nodes, so name what you changed with `--treenode-filter "/*/*/<class>/*"`. The GCS tests use the bucket `MIDA_TEST_BUCKET` names, else fake-gcs-server through Testcontainers, and skip without either. The benchmarks and the real-repo builds are `[Explicit]` tests, see [docs/notes/benchmarks.md](docs/notes/benchmarks.md). A release is a `v<version>` tag, which packs the tool and pushes it to nuget.org.
