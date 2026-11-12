# Cache projects with static web assets

Status: built 2026-11-12, from a plan written the same day. Code: `ProjectOutputs.Relocation` and `IsTokenized` (`src/Mida/ProjectOutputs.cs`), `Relocation` and `ManifestFile.Tokenized` (`src/Mida.Store/Manifest.cs`), the `dependency-assets` key line (`src/Mida/KeyComputer.cs`).

## Why

Until this, a project whose static web asset manifests named the checkout was never stored. It always missed, and so did every project that got a copy of such a manifest in its `bin/`. On real repos that was a large share of the graph ([benchmarks.md](benchmarks.md#real-world-repos)):

| repo | keys | misses in an unchanged clean clone, before |
|---|---|---|
| Abp 10.7.1, framework | 163 | 41, all of them this: 33 Web and Razor SDK projects and 8 that copy their manifests |
| OrchardCore v3.0.1 | 448 | 109, not looked at, most likely this |

On SDK 11 every Web SDK project has framework assets, so every one of them missed.

## What was built

**Stored blobs hold tokens, and a restore expands them.** A save replaces each root in these files with its token before hashing and storing, and marks the file in the manifest (`"tokenized": true`). A restore verifies the blob's hash, which is of the stored bytes, and then puts this checkout's roots back. No other file is ever rewritten. The roots and tokens are `PathTokens`' (`src/Mida/PathTokens.cs`): repo, package folder, .NET install and user profile. `Mida.Store` knows nothing of MSBuild: `OutputCache` takes a `Relocation`, a pair of functions, from `BuildRunner`. A manifest written before the mark reads as unmarked, and the mark is left out of the JSON when false, so other entries' bytes did not change.

The files are chosen by name, never by content, since a PDB holds the root too:

| file | where | who reads it |
|---|---|---|
| `<Project>.staticwebassets.runtime.json` | `bin/`, and copied into the `bin/` of every project above | the app at startup in Development, and `WebApplicationFactory` |
| `staticwebassets.build.json` | `obj/` | builds of dependents, `dotnet publish --no-build` |
| `staticwebassets.development.json` | `obj/` | the source of the runtime manifest |
| `staticwebassets.pack.json` | `obj/`, Razor class libraries | `dotnet pack --no-build` |
| `MvcTestingAppManifest.json` | `obj/` and `bin/` of a project that references `Microsoft.AspNetCore.Mvc.Testing` | `WebApplicationFactory`, for the host's content root |

`MvcTestingAppManifest.json` was not in the plan. The reproduction found it: it maps each referenced host to its absolute project directory, and `WebApplicationFactory` uses that path without checking it exists. It was stored as it was before this change whenever the host had no static web assets, so such a test project could hit and run against the writer's checkout. Its writer escapes each slash (`\/`), so `PathTokens` now has that spelling on Unix, as it has the doubled backslash on Windows.

**The safety net.** Every `*staticwebassets*.json` and every file above is checked after tokenizing. A JSON string that is a fully qualified path that exists means something no token covers, such as an asset linked in from outside the repo. Then the project is not stored, and the build warns: `Outputs of <project> not stored: <file> names <path>, which no token covers`. "Exists" is there because base64 hashes in these files can start with a slash. `/` alone is a base path. The rule stays: the store never holds a writer's path.

**The incremental caches are not stored**: `*.dswa.cache.json`, `staticwebassets.build.json.cache` and `swae.build.ex.cache` differ per checkout and only an incremental build reads them. `dotnet test --no-build` and `dotnet publish --no-build` work on a hit without them.

**A key line for a dependency's assets.** A host's manifests hold the fingerprint, integrity and compressed name of each asset of its dependencies. A body edit of a library's `wwwroot/a.css`, or of its scoped CSS, changes neither its reference assembly nor its file names, so the host hit with the old integrity: the test below failed exactly so before the line. Now every transitive dependency with a `*.staticwebassets.endpoints.json` in its `bin/` adds `dependency-assets <name> <hash>`, the hash of those files without their `Last-Modified` headers, which are the sources' times. The plan suggested the build manifest in `obj/` instead. The endpoints manifest names no path, and it is already among the dependency's outputs the key reads. A C# body edit of a library still stops at it. The cost: a project above a host, such as its test project, also misses on an asset edit.

**Copies in dependents** needed no new code. A test project's copy of `Web.staticwebassets.runtime.json` is byte-identical to the host's, both expanded, so it is a `ManifestReference` taken from the host on every hit.

**Times are the writer's.** A hit restores the writer's `LastWriteTime` in `staticwebassets.build.json` and `Last-Modified` in the endpoints manifests. ETag and integrity come from content, so what is served is right. Two writers of one key store different manifests, so the second gets "another build stored different outputs for this key". Writers race rarely, since the second run of a key hits. `mida build` and `dotnet build` differ in these files in every clone.

## Decided

The user decided both on 2026-11-12.

1. **SDK 11's hot reload key pair is left out of the cache.** `browser-tools-key.private.json` and `.public.json` in `obj/.../HotReload/` are new on every Debug build of a Web SDK project. The SDK's `EnsureHotReloadBrowserToolsKey` writes them, and its targets name `Microsoft.DotNet.Watch.BrowserToolsBuildOutputs` as the reader, so `dotnet watch`, which builds anyway. A private key does not belong in a shared bucket. The asset that embeds the public key is stored as built.
2. **A non-deterministic asset is stored.** On SDK 11 the fingerprint of that generated asset is new on every build. Every hit serves the writer's, which is consistent within the entry.

## Measured

On a three-project repo (a Razor class library with `wwwroot/a.css` and scoped CSS, a Web SDK host with `wwwroot/site.js` that references it and the QuickGrid package, and a project over the host) built at two paths with `-p:ContinuousIntegrationBuild=true` on SDK 10.0.404 and 11.0.100, macOS arm64, `net10.0`:

- The checkout's path is in the four manifests above, in the `*.dswa.cache.json` files and in the host's copies. The package folder is in the host's runtime, build and development manifests and its `rbcswa.dswa.cache.json`. With both replaced by a token, every runtime, development and pack manifest is the same at both paths on SDK 10.
- Times differ: `staticwebassets.build.json` (with its `Hash`), the endpoints manifests in `bin/` and `obj/`, and `obj/staticwebassets/msbuild.*.props` of a Razor class library, which hold no path.
- SDK 11 adds the key pair and the asset embedding it. That asset's fingerprint differs between two builds of the same source, and with it the host's runtime, build and endpoints manifests.
- No file named the .NET install. A `net11.0` project's framework assets would.

## Checks

TUnit, in `tests/Mida.Tests`, each on `net10.0` (SDK 10) and `net11.0` (SDK 11):

- `CacheEndToEndTests.WebCleanCloneAtAnotherPathIsAllHitsAndServesItsAssets`: build, delete the writer's checkout, build a clone at a deeper path: all hits. The clone's runtime manifest names the clone. No text blob names the writer's checkout or the package folder; assemblies do, since the fixture is not a git repo. The host, started with `dotnet Web.dll` in Development, serves its own asset, the library's, the library's scoped CSS and the package's, each by its plain and fingerprinted URL, with bytes that match the integrity in its manifest. `dotnet test --no-build` runs a `WebApplicationFactory` test that fetches two of them. `dotnet publish --no-build` puts both under `wwwroot/`.
- `WebCloneOnAnotherMachineServesItsPackagesAssets`: a clone with its own `NUGET_PACKAGES` hits, and its runtime manifest names that folder.
- `EditedAssetIsServedWithItsNewIntegrity`: an edit of the host's asset, the library's asset or its scoped CSS misses what it must and is served with its new integrity. Once when the library misses, and once when it hits from a newer entry than its host.
- `StaticWebAssetOutsideEveryRootIsNotStoredAndSaysSo`: a library that links an asset from outside the repo is not stored, nor is its host, and the build says why.
- `ProjectOutputsTests` and `OutputCacheTests`: tokens out and back at another machine's paths, the escaped slash, files left alone, the safety net, a manifest without the mark.

Mutations, each failing only its own tests: no expansion on restore fails every test that restores a tokenized file; no tokens on save fails the round trips and the hits; no safety net fails the two tests of it; no `dependency-assets` line fails the stale tests, the library's ones on their integrity.

## Real repos

`RealWorld/Abp` in the OrbStack container, four pinned cores, ext4, SDK 10.0.402 and 11.0.100: a clean clone at another path went from 122 hits and 41 misses to 163 hits and 0 misses, and from 1.16 to 0.75 of a plain build's time. Its `bin/` comparison passed. Numbers in [benchmarks.md](benchmarks.md#real-world-repos). The writers' times did not cause "different outputs" there, since each key was written once.

## Not done

- `RealWorld/OrchardCore` was not run. Its 109 misses are expected to be mostly this, which is not checked.
- Windows has never been run. The doubled-backslash spelling is in `PathTokens` and the unit tests build their JSON with the serializer, so they cover it on Windows, but no build has.
- A checkout whose roots are not `PathTokens.Plain`, holding a character such as `&`, `'` or `%`, does not expand these files, and a project with them misses with a warning. Only `"`, `\` and control characters would break the JSON, so this is stricter than it needs to be.
- A path in these manifests that no longer exists at save time is not caught by the net.
