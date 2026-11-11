# Plan: cache projects with static web assets

Status: plan, written 2026-11-12. Nothing here is built. It is written for whoever implements it, with no other context than this repo.

## Why

A project whose static web asset manifests name the checkout is never stored (`ProjectOutputs.HoldCheckoutPaths`, [local-store.md](local-store.md)). It always misses, and so does every project that gets a copy of such a manifest in its `bin/`. On real repos that is a large share of the graph ([benchmarks.md](benchmarks.md#real-world-repos)):

| repo | keys | misses in an unchanged clean clone |
|---|---|---|
| Abp 10.7.1, framework | 163 | 41, all of them this: 33 Web and Razor SDK projects and 8 that copy their manifests |
| OrchardCore v3.0.1 | 448 | 109, not yet looked at, most likely this |

On SDK 11 every Web SDK project has framework assets, so every one of them misses.

## Goal

A clean clone at another path, on another machine, hits for these projects, and the app then serves its static files from the clone. A changed asset is never served stale.

Done when:

1. `StaticWebAssetsAreNotStoredAndOtherProjectsAre` is replaced by tests that pass on `net10.0` (SDK 10) and `net11.0` (SDK 11): see [Tests](#tests).
2. `RealWorld/Abp` in the OrbStack container reports no miss with the reason "static web assets", and its output comparison still passes.
3. The docs listed under [Docs](#docs) say what is true.

## What the files are

Measured 2026-11-12 on a three-project repo (a Razor class library `Rcl` with `wwwroot/a.css`, a Web SDK host `Web` with `wwwroot/site.js` that references it, and a plain project `Tests` that references `Web`), built at two paths with `-p:ContinuousIntegrationBuild=true` on SDK 10.0.404 and 11.0.100. Reproduce it before relying on it.

Files of cached kinds that hold the checkout's absolute path:

| file | where | who reads it |
|---|---|---|
| `<Project>.staticwebassets.runtime.json` | `bin/`, and copied into the `bin/` of every project above | the app at startup in Development, and `WebApplicationFactory` |
| `staticwebassets.build.json` | `obj/` | builds of dependents, `dotnet publish --no-build`, incremental builds |
| `staticwebassets.development.json` | `obj/` | the source of the runtime manifest |
| `staticwebassets.pack.json` | `obj/`, Razor class libraries | `dotnet pack --no-build` |
| `rbcswa.dswa.cache.json`, `rpswa.dswa.cache.json` and other `*.dswa.cache.json` | `obj/` | incremental builds only, as far as seen |

The path is a plain JSON string: `ContentRoots`, `Identity`, `ContentRoot`, `RelatedAsset`, `OriginalItemSpec` and the like. On Windows JSON escapes each backslash, which `HoldCheckoutPaths` already allows for.

With the path replaced by a token, these are the same in both checkouts:

- SDK 10: every `*.staticwebassets.runtime.json`, `staticwebassets.development.json` and `staticwebassets.pack.json`. The compressed assets in `obj/.../compressed/` have the same names.
- So a token is enough for what the app reads on SDK 10.

What still differs, and is not the path:

- **Times.** `staticwebassets.build.json` holds each asset's `LastWriteTime`, and `<Project>.staticwebassets.endpoints.json` in `bin/` holds it as a `Last-Modified` response header. They are the modification times of the source files, so they differ in every clone. `staticwebassets.build.json` also has a `Hash` over its content, which differs with them.
- **`*.dswa.cache.json`** keys its entries by a hash that differs per checkout.
- **SDK 11 only: a key pair made by every build.** `obj/.../HotReload/browser-tools-key.private.json` and `.public.json` are new on every build. The public key is written into the generated asset `_framework/Microsoft.NET.Sdk.BlazorWeb.HotReload.BrowserTools.Config.js`, so that asset's fingerprint, integrity and compressed file name differ between two builds of the same source, and with them the runtime manifest, `staticwebassets.build.json` and the endpoints manifest. This is the build not being deterministic, with or without mida.

Not seen in this sample, and to check on Abp, which uses Razor class libraries from packages: content roots under the NuGet package folder or under the dotnet root. `HoldCheckoutPaths` looks only for the repo root, so a project whose manifests name only the package folder is stored today with the writer's package folder in it. That is a bug on another machine, whatever else this plan does.

## Design

**Stored blobs hold tokens, and a restore expands them.** For the files above, a save replaces each absolute root with its token before hashing and storing, and a restore writes the blob and then puts this checkout's roots back. The manifest marks such a file, so a restore knows to expand it and nothing else is ever rewritten.

- Use `PathTokens` (`src/Mida/PathTokens.cs`) for the roots: repo root, package folder, dotnet root, user profile. `TargetResults` already does the same for replayed items with `{RepoRoot}` and `{NuGetPackageRoot}`. One set of tokens for both would be better than two.
- `Mida.Store` knows nothing of MSBuild and must stay that way. `OutputCache` should take the rewrite from its caller, for example as a pair of functions given a relative path and the bytes, and should only know that a `ManifestFile` is marked.
- `ManifestFile` gets an optional property for the mark. The JSON options reject missing required properties, so give it a default, as `Manifest.BuildTime` has, and check that a manifest written before this change still reads.
- `RestoreAsync` verifies a blob by hashing the restored file. Verify first, expand after: the hash is of the stored bytes.
- Choose the files by name, in one place next to `ProjectOutputs.IsCached`: `*.staticwebassets.runtime.json`, `staticwebassets.build.json`, `staticwebassets.development.json`, `staticwebassets.pack.json`, and what else the reproduction turns up. Never by "contains the root": a PDB without a path map contains it too, and replacing bytes in a binary breaks it.
- JSON escapes backslashes. Tokenize the escaped form too, or Windows stores nothing. Windows has never been run, so say so in the docs.

**Keep the safety net.** After tokenizing, a JSON file of these kinds that still holds an absolute path under any root, or the checkout's path in any spelling, means something was missed. Then do not store the project and say why, as today. `HoldCheckoutPaths` becomes that check. The rule stays: the store never holds a writer's path.

**Stop storing the incremental caches.** Add `*.dswa.cache.json`, `staticwebassets.build.json.cache` and `swae.build.ex.cache` to the exclusions in `ProjectOutputs.IsCached`, beside the other caches that only an incremental build reads. First check that claim: `dotnet test --no-build` and `dotnet publish --no-build` must work on a hit without them.

**Copies in dependents need no new code, but check it.** A dependent's copy of `Rcl.staticwebassets.runtime.json` should become a `ManifestReference`, which a hit copies from the dependency's current, already expanded file. `KeyComputer.DependencyCopies` matches by relative path and bytes of local files, which are both expanded. If a dependent's copy is not byte-identical to the dependency's, it is the dependent's own file and goes through the token path, which is why files are chosen by name and not by project.

**Times: take the writer's.** A hit restores the writer's `LastWriteTime` and `Last-Modified`. The ETag and integrity come from content, so what is served is right. Two things follow, both to state in the docs and neither to solve now:

- Two writers of one key store different manifests, so the second gets "another build stored different outputs for this key". Writers race rarely, since the second run of a key hits. If the warning shows up in the tests or on Abp, the times are the first suspect.
- `mida build` and `dotnet build` differ in these two files in every clone. `RealWorld` already leaves out what differs between two plain builds.

## Decided

The user decided both on 2026-11-12.

1. **SDK 11's hot reload key pair is left out of the cache.** Storing a project would store `browser-tools-key.private.json`, a private key, in a shared bucket, and every reader would get the same pair. So neither key file is stored, and the asset that embeds the public key is kept as built. First find out what reads the key files (most likely only `dotnet watch`). If something a CI run does after a hit needs them, stop and ask. Do not inject a property that turns the browser tools asset off: that would change what a plain build produces.
2. **A non-deterministic asset is stored.** On SDK 11 the fingerprint of that generated asset is new on every build. Every hit serves the writer's, which is consistent within the entry.

## The stale trap to close first

A host's manifests describe its dependencies' assets: the fingerprint, integrity and compressed file of `Rcl/wwwroot/a.css` are in `Web`'s runtime, build and endpoints manifests. Today a body edit of `a.css` changes none of the lines a host's key takes from a dependency:

- `dependency-ref`: the reference assembly of `Rcl` does not change.
- `dependency-files`: the names in `Rcl/bin/` do not change.

So unless the predictor already makes `Rcl/wwwroot/a.css` an input of `Web`, the host hits with a manifest that names the old fingerprint, and the app serves a wrong ETag or a 404 for the fingerprinted URL. Until now this could not happen, because such a host was never stored.

Write this test before the feature, and expect it to fail once hosts are stored: edit `Rcl/wwwroot/a.css` in a built clone, build, start the host, and fetch the asset by its plain and its fingerprinted URL.

If it fails, the key needs the dependency's assets. Options, the first preferred:

1. A `dependency-assets <name> <hash>` line for every dependency in the closure that has a static web asset build manifest: the hash of its `staticwebassets.build.json` with the times and `Hash` left out, or of the asset list that manifest names. It is read after the dependency has finished, as the reference assembly is.
2. A `dependency-key` line for such dependencies, as a build-time reference has. Simpler, but every body edit of a Razor class library then misses its hosts, which gives up early cutoff for them.

The same question holds for a dependency's scoped CSS (`*.razor.css`, bundled into `<Project>.styles.css`) and JS modules. Cover one of them in the test.

## Steps

1. Reproduce the tables above on both SDKs, with a package Razor class library added to the sample, and correct this note where it is wrong.
2. Write the tests below against today's code. The clean-clone ones fail with "not stored". Keep them.
3. Tokens on save and restore, the manifest mark and the safety net. The clean-clone tests pass on SDK 10.
4. The stale trap: the test, then the key line if it fails.
5. The cache exclusions, after checking `--no-build` test and publish.
6. SDK 11: the key files left out, as decided above.
7. `RealWorld/Abp`, then `RealWorld/OrchardCore`, in the container. Record the numbers in [benchmarks.md](benchmarks.md).
8. Mutation checks, as [local-store.md](local-store.md#checks) lists for earlier work: no expansion on restore, no tokenizing on save, no safety net, no new key line. Each must fail only its own tests.
9. Docs.

## Tests

All in TUnit, in `tests/Mida.Tests/CacheEndToEndTests.cs`, building on `WebRepo`, which needs a Razor class library with an asset and a test project over the host. Each runs on both target frameworks, which is how both SDKs get covered.

- **Clean clone.** Build at `a`, delete `a`, build a clone at a deeper path: all hits. The clone's `Web.staticwebassets.runtime.json` names the clone and not `a`. No blob in the cache directory contains `a`'s path or the package folder.
- **The app serves.** From the clone, with `a` deleted, start the host in Development without building (`dotnet Web.dll` in `bin/`, or `dotnet run --no-build`), and fetch `site.js` and `_content/Rcl/a.css`: 200 with the clone's content. Use port 0 and read the port from the output, and kill the process in a `finally`.
- **`WebApplicationFactory`.** The test project over the host fetches the same two files, run with `dotnet test --no-build` on the clone. This is the case that crashed with `DirectoryNotFoundException` when relocation was first tried.
- **Another machine.** The clone built with another `NUGET_PACKAGES` folder, as `CloneOnAnotherMachineHitsThroughTheBucket` does, with a package that brings static web assets.
- **Stale checks.** After an edit of the host's own asset, of the library's asset, and of a scoped CSS file: the build misses what it must, and the served content and its fingerprinted URL are the new ones. Once when the library misses, and once when it hits from a newer entry than its host, as `EditReachesTestsWhenTheLibraryHitsFromANewerEntryThanItsDependents` does.
- **Publish.** `dotnet publish --no-build` on the clone puts both assets under `wwwroot/`.
- **The safety net.** A manifest of these kinds with an absolute path that no token covers is not stored, and the build says so.
- **Old manifests.** A manifest without the new property still restores.

## Docs

- `README.md`, Limits: drop the static web assets line, or narrow it to what is left.
- [local-store.md](local-store.md): the decision "A project with static web assets is not stored", the known limit, and the mutation list.
- [relocation.md](relocation.md): the correction that says file contents are never rewritten, and the table of what goes in the manifest.
- [cache-key.md](cache-key.md): the new key line, if there is one.
- [benchmarks.md](benchmarks.md): the Abp and OrchardCore numbers.
- This note: turn it from a plan into what was built and what was measured.

## Working rules of this repo

- Tests are TUnit, also in generated fixtures.
- Never run the whole suite or the 1024-project benchmark on the user's machine. Run filtered tests (`--treenode-filter`), and heavy runs in the OrbStack container pinned to four cores, on an ext4 loop mount.
- Change files with the editor tools, not through shell commands.
- Commit on `main` only when asked, and do not push unless asked.

## Known before starting

- In that container, on a clean clone at another path, the stored graph is not reused ("Graph evaluated: the path of the checkout changed"), and 22 end-to-end tests fail on that line. The same tests pass on macOS. Not explained as of 2026-11-12. It does not change hits and misses, but tests that also assert "Graph from the store" will fail there for that reason.
- `RealWorld` compares only `bin/`. Most of what this plan rewrites is in `obj/`, so the end-to-end tests above are what proves it, not that comparison.
- OrchardCore's own plain builds are not relocatable: its assemblies hold the path of their PDB. That is separate from this plan and limits what its comparison shows.
