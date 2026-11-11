# MIDA - .NET Monorepo Multi Tool

`mida build` builds a .NET repo through MSBuild's static graph with an output cache on local disk and Google Cloud Storage. Each project gets a key from its inputs. On a hit its outputs are restored instead of built, at any checkout path, so a clean CI clone of an unchanged repo restores and builds nothing. It is a CI tool: the target is CI build and test time.

Status: an MVP. It has run on small repos on macOS and Linux arm64. See [docs/plan.md](docs/plan.md) for what is proven and the known risks.

## Install

```sh
dotnet tool install -g mida
```

It needs .NET SDK 10 or 11, and it loads MSBuild from the SDK your `global.json` selects. mida must run on that SDK's runtime, because a runtime cannot load a newer SDK's MSBuild. If it cannot, it stops with an error.

## Use

```sh
mida build [<solution>] [-c <configuration>] [-p:<name>=<value>] [--cache <dir>] [--cache gs://<bucket>[/<prefix>]] [--read-only]
```

- `<solution>` defaults to the only `.sln` or `.slnx` in the current directory.
- `-p` takes one property each. Unlike `dotnet build`, it does not split on `;`.
- `--cache <dir>` is the local store, `~/.mida/cache` by default. Nothing evicts it.
- `--cache gs://...` adds a GCS store behind the local one. Hits restore from local disk, and what came from the bucket is kept locally.
- `--read-only` stores nothing in either store.

It restores, then builds, and ends with a summary such as `47 hits, 2 misses, 312.4 s of project build time saved`. The time saved is the stored build time of each hit, summed over projects, so it is not wall-clock time. A key that cannot be computed and any store failure are misses with a warning: the cache never fails a build.

`mida build` always sets `ContinuousIntegrationBuild=true` and `IncludeSourceRevisionInInformationalVersion=false`, which make outputs the same at every checkout path and keep the commit SHA out of reference assemblies. A `-p` for either wins, at the cost of hits.

It restores with NuGet's static graph evaluation, which is faster and which NuGet calls experimental. `-p:RestoreUseStaticGraphEvaluation=false` restores the default way.

## CI

- Run every later step with `--no-build` and the same configuration, such as `dotnet test -c Release --no-build`. A step without it rebuilds everything.
- Restore is not cached. Cache `~/.nuget/packages` with your CI's own cache.
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

## Limits

- Projects that target several frameworks are not cached. Their dependents are, and so is a project with one framework in `TargetFrameworks`.
- Web SDK projects with static web assets are not cached, since their manifests name the checkout.
- Environment variables are not in the key.
- Tools that stamp a version from git, such as MinVer, NBGV and GitVersion, cost the hits of every dependent whenever they change `AssemblyVersion`: MinVer on a major version, NBGV on a minor version and GitVersion on a patch by default. A hit can serve outputs stamped with an older version, which matters at pack and publish.

## Develop

```sh
dotnet test --project tests/Mida.Tests -f net10.0
dotnet test --project tests/Mida.Tests -f net11.0
```

The GCS tests start fake-gcs-server through Testcontainers and skip without a Docker that runs Linux containers. A release is a `v<version>` tag, which packs the tool and pushes it to nuget.org.
