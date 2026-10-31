# Versioning, release management, NuGet packaging and CI/CD patterns for .NET monorepos (state as of Oct 2026)

Method note: Most repo-level findings below come from reading the actual files on each repo's default branch via the GitHub API on 2026-10-28, so they reflect the current state. Tool-doc findings come from official docs fetched the same day. Anything marked "(not re-verified this session)" is background knowledge that was not re-checked, and the report writer should treat it with care.

## 1. Versioning tools for multi-package .NET repos (NBGV, GitVersion, MinVer, release-please, changesets ports, Nx, conventional commits; independent vs lockstep)

### Takeaway
No .NET versioning tool natively provides "monorepo release orchestration" (pick changed packages, bump them, cascade to dependents, write changelogs, tag, publish). The tools cover single pieces. MinVer gives per-package tag prefixes, NBGV gives per-directory version.json with path filters, and GitVersion only has regex ignore paths. Release-please still has no C# release type. Changesets-style .NET ports exist but are tiny 0.x projects. Large real repos (OpenTelemetry, Azure SDK, Aspire) fill the gap with home-grown PowerShell/MSBuild scripts.

### Cited Findings
**MinVer (adamralph/minver)**
- The current NuGet package is MinVer 7.0.0. — [NuGet: MinVer](https://www.nuget.org/packages/MinVer)
- Monorepo support is the `MinVerTagPrefix` MSBuild property (case-insensitive; e.g. `ext-` makes a project respond only to tags like `ext-1.0.0`). The CLI equivalent is `-t` / `--tag-prefix`. MinVer derives versions from tags only and ignores branches. If you don't set `MinVerAutoIncrement`, the patch auto-increments after an RTM tag. `MinVerDefaultPreReleasePhase` configures the prerelease label. — [MinVer docs (search summary)](https://docsearch.algolia.com/mcp/docs/repo/adamralph/minver); [NuGet: MinVer](https://www.nuget.org/packages/MinVer)
- A May 2026 walkthrough of an independent multi-package MinVer setup:
  - Each csproj sets `MinVerTagPrefix` (e.g. `M.Super.Extensions-v`), MinVer is added once in `Directory.Build.targets`, and `MinVerMinimumMajorMinor=1.0`.
  - `release.yml` triggers on per-package tag patterns. A shell script maps tag → project folder, the job builds and tests the whole solution, then packs only the tagged project and checks that the .nupkg name matches the tag.
  - **Key pitfall:** a `ProjectReference` between independently versioned packages makes the emitted dependency float to an unpublished prerelease version. The workaround is to look up the dependency's latest tag and pass it in as `MinVerVersionOverride`, which forces "release dependency before dependent".
  - Other pitfalls: you need `fetch-depth: 0`, and tag-triggered workflows only fire if the workflow file exists on the default branch (GitHub doesn't replay missed events).
  - [Start Debugging, 2026-05](https://startdebugging.net/2026/05/independently-release-multiple-nuget-packages-with-minver-and-trusted-publishing/)

**Nerdbank.GitVersioning (NBGV, dotnet org)**
- Version height comes from git history, governed by `pathFilters` in `version.json`. Without filters the implied filter is `":/"`, so any commit anywhere bumps every project. Filter syntax:
  - paths relative to the version.json (`./`, `../Shared`)
  - repo-root paths (`/file`, `:/dir`)
  - exclusions with `:!` or `:^`
  - `*`, `?` and `**` wildcards
  - Matching follows `core.ignorecase`.
  - [NBGV path filters docs](https://dotnet.github.io/Nerdbank.GitVersioning/docs/path-filters.html)
- Newer CLI commands `nbgv path-filters check` (CI lint; exits non-zero on mismatch) and `nbgv path-filters update` compute filters from the MSBuild project graph. Only `.csproj`/`.vbproj` are included by default (`--ext` adds `.fsproj` etc.), and files in `.gitignore` are excluded. Project discovery under a version.json "stops searching for projects at directories containing their own version.json files". This is how per-directory version.json files partition a monorepo. — [NBGV path filters docs](https://dotnet.github.io/Nerdbank.GitVersioning/docs/path-filters.html)
- Limitation: a version.json with no MSBuild projects is skipped, and whole project directories are included, so any source change under them bumps the height. — [NBGV path filters docs](https://dotnet.github.io/Nerdbank.GitVersioning/docs/path-filters.html)
- NBGV is commonly cited as the tool with built-in path-filtered height, which GitVersion users ask for. — [GitVersion discussion #4547](https://gh.r-mj.com/GitTools/GitVersion/discussions/4547)

**GitVersion (GitTools)**
- Monorepo support is limited to `ignore.paths` regexes, documented in PR #4420 (merged 2025-06-28):
  - Example: `^(?!\/ProductA\/|\/LibraryC\/).*` ignores everything except one product plus a shared lib.
  - **Caveat:** a commit is ignored only if *every* path it changed matches. Mixed commits still count.
  - Matching is case-sensitive unless you use `(?i)`.
  - The docs' prose and examples use inconsistent names.
  - [GitVersion PR #4420](https://github.com/GitTools/GitVersion/pull/4420/files)
- A GitVersion discussion (#4547) on per-package monorepo versions points to a still-pending "ignoring paths in git tree" feature. It quotes a 2020 maintainer statement that monorepos aren't a standard GitVersion use case, and no maintainer reversal was found. — [GitVersion discussion #4547](https://gh.r-mj.com/GitTools/GitVersion/discussions/4547)
- GitVersion has no per-project tag-prefix-per-csproj model comparable to MinVer's. Each GitVersion run computes a single version for the repo/config. (not re-verified this session; inferred from the docs above)

**release-please (googleapis)**
- The supported release types are dart, elixir, go, helm, java, krm-blueprint, maven, node, expo, ocaml, php, python, ruby, rust, sfdx, simple, terraform-module. **There is no csharp/dotnet type.**
- .NET users must use `simple` plus `extra-files` with the generic XML updater `{ "type": "xml", "path": "...", "xpath": "//..." }` pointing at the csproj `<Version>` element, or use inline `x-release-please-version` annotations.
- Monorepo use is "manifest releases" (`release-please-config.json` + `.release-please-manifest.json`).
- [release-please customizing.md](https://github.com/googleapis/release-please/blob/main/docs/customizing.md)
- A Microsoft ISE blog shows release-please manifest mode with `monorepo-tags: true` for independent release cycles (example uses the python type). — [Microsoft ISE devblog](https://devblogs.microsoft.com/ise/?p=15257)

**Changesets-style tools for .NET (all small, early-stage)**
- **SolarWinds.Changesets:** a .NET tool (`changeset` command) porting `@changesets/cli` (init/add/version/publish to a NuGet feed). v0.1.4, roughly 982 downloads, last updated around December 2025. — [NuGet: SolarWinds.Changesets](https://www.nuget.org/packages/SolarWinds.Changesets); [GitHub solarwinds/net-changesets](https://gh.hhj520.top/solarwinds/net-changesets)
- **Versa:** v0.1.0, a "native port of Changesets" for Go/.NET/Node, with only `versa publish` so far. — [NuGet: versa](https://www.nuget.org/packages/versa)
- **MonoVer:** v0.1.3, with `monover new/version/publish`. — [NuGet: MonoVer](https://www.nuget.org/packages/monover)
- **Cxset:** `.changes/` files that edit csproj `<Version>`, run as `dnx cxset add` (uses the .NET 10 `dnx` tool runner). — [libraries.io Cxset](https://libraries.io/nuget/Cxset)
- **changepacks:** a Rust changesets-inspired tool with a C#/NuGet backend (`changepacks-csharp` 0.3.x). — [docs.rs changepacks-csharp](https://docs.rs/crate/changepacks-csharp/0.3.3)
- The upstream changesets project has a discussion proposing a `dotnet changeset add/version` port. — [changesets discussion #1329](https://github-redirect.dependabot.com/changesets/changesets/discussions/1329)
- Related affected-detection tool: `dotnet-affected` (leonardochaia). — [GitHub dotnet-affected](https://github.com/leonardochaia/dotnet-affected)

**Nx with .NET**
- The official `@nx/dotnet` plugin (npm, Nx 22+) replaces the deprecated community `@nx-dotnet/core`, and a migration guide exists. Nx's migrations list shows 23.0.x entries. — [Nx: migrate from @nx-dotnet/core](https://nx.dev/docs/technologies/dotnet/guides/migrate-from-nx-dotnet-core); [Nx @nx/dotnet migrations](https://nx.dev/docs/technologies/dotnet/migrations)
- Whether `nx release` has a first-class NuGet/csproj version-actions implementation was not confirmed. See Gaps.

**What real repos actually do (versioning model)**
- **opentelemetry-dotnet (core) — lockstep groups via MinVer tag prefixes:**
  - The publish workflow triggers on tags `core-*`, `coreunstable-*`, `declarativeconfig-*`. All stable core packages share one version, unstable packages share another (e.g. `coreunstable-1.20.1-beta.1`).
  - MSBuild sets `ExposeExperimentalFeatures` based on whether `MinVerPreRelease` is empty.
  - [otel-dotnet publish-packages-1.0.yml](https://github.com/open-telemetry/opentelemetry-dotnet/blob/main/.github/workflows/publish-packages-1.0.yml); [otel-dotnet build/Common.prod.props](https://github.com/open-telemetry/opentelemetry-dotnet/blob/main/build/Common.prod.props); [tags](https://github.com/open-telemetry/opentelemetry-dotnet/tags)
- **opentelemetry-dotnet-contrib — fully independent per component via MinVer tag prefixes:**
  - Each csproj sets e.g. `<MinVerTagPrefix>Instrumentation.Http-</MinVerTagPrefix>`, and tags look like `Sampler.AWS-0.1.0-alpha.17`.
  - [Instrumentation.Http csproj](https://github.com/open-telemetry/opentelemetry-dotnet-contrib/blob/main/src/OpenTelemetry.Instrumentation.Http/OpenTelemetry.Instrumentation.Http.csproj); [contrib tags](https://github.com/open-telemetry/opentelemetry-dotnet-contrib/tags)
- **Azure SDK for .NET — explicit `<Version>` in each csproj, edited by scripts (no git-derived versioning):**
  - Example: `Azure.Storage.Blobs.csproj` has `<Version>12.31.0-beta.1</Version>` and `<ApiCompatVersion>12.30.1</ApiCompatVersion>` ("managed automatically").
  - `eng/Versioning.targets` strips the prerelease and appends `-alpha.$(OfficialBuildId)` (format `yyyyMMdd.r`) for dev/nightly builds unless `SkipDevBuildNumber=true`. It also computes an increasing 4-part FileVersion (Arcade CoreVersioning spec).
  - Scripts include `eng/scripts/Update-PkgVersion.ps1`, `Prepare-Release-NonInteractive.ps1`, `Collect-Unreleased.ps1` and `Remove-ObsoleteApiCompatBaselines.ps1`.
  - [Azure.Storage.Blobs.csproj](https://github.com/Azure/azure-sdk-for-net/blob/main/sdk/storage/Azure.Storage.Blobs/src/Azure.Storage.Blobs.csproj); [eng/Versioning.targets](https://github.com/Azure/azure-sdk-for-net/blob/main/eng/Versioning.targets); [eng/scripts](https://github.com/Azure/azure-sdk-for-net/tree/main/eng/scripts)
- **Aspire (repo now `microsoft/aspire`) — Arcade-style single lockstep version in `eng/Versions.props`:**
  - Current main: `MajorVersion` 17, `MinorVersion` 0, `PatchVersion` 0, `PreReleaseVersionLabel` preview.1.
  - `StabilizePackageVersion` → `DotNetFinalVersionKind=release`.
  - `eng/Version.Details.xml` handles dependency flow (Arcade/Maestro).
  - Note: "17" is quoted verbatim from the file. I didn't verify whether it maps to the marketed Aspire version.
  - [microsoft/aspire eng/Versions.props](https://github.com/microsoft/aspire/blob/main/eng/Versions.props); [eng/](https://github.com/microsoft/aspire/tree/main/eng)
- **Polly — single lockstep version for all packages:**
  - The build workflow triggers on any tag (`tags: ['*']`).
  - Scripts in `eng/`: `bump-version.ps1`, `update-changelog.ps1`, `update-baselines.ps1`.
  - [Polly build.yml](https://github.com/App-vNext/Polly/blob/main/.github/workflows/build.yml); [Polly eng/](https://github.com/App-vNext/Polly/tree/main/eng)
- **opentelemetry-dotnet-contrib release automation:**
  - A `prepare-release.yml` workflow_dispatch takes `component` (choice) and `version` inputs and opens a release PR.
  - Companion workflows: `post-release.yml`, `core-version-update.yml`, `unreleased-changes-report.yml`, `check-for-new-sdk-releases.yml`.
  - Releases are human-initiated per component, not commit-message driven.
  - [contrib prepare-release.yml](https://github.com/open-telemetry/opentelemetry-dotnet-contrib/blob/main/.github/workflows/prepare-release.yml); [contrib workflows dir](https://github.com/open-telemetry/opentelemetry-dotnet-contrib/tree/main/.github/workflows)

### Inferences
- **The dominant real-world patterns:**
  - (a) MinVer tag prefix per package or package-group plus a tag-triggered publish workflow that resolves tag → project (OTel).
  - (b) An explicit `<Version>` in the csproj, managed by scripts, plus dev-build suffixes (Azure SDK).
  - (c) A central `Versions.props` lockstep (Arcade: Aspire, Polly).
  - None use conventional commits to drive bumps. Release intent is explicit: a manual workflow_dispatch or a script-edited version.
- Independent versioning creates the hardest problem a new tool must solve: **inter-package dependency version pinning at pack time** (ProjectReference → package dependency version). MinVer/NBGV compute each project's version in isolation. The pack of a dependent uses whatever version the referenced project computes in *that* build, which may be an unreleased prerelease. Lockstep avoids this entirely, which is likely why OTel core, Polly and Aspire stay lockstep.
- OTel's hybrid, lockstep within a group and independent across groups, shows that "release groups" (like Nx/changesets `fixed`/`linked` groups) map well onto `MinVerTagPrefix` shared across several csprojs.
- An opportunity: no tool combines changesets-style intent files, MSBuild-graph-aware dependent cascading, and per-package tag prefixes compatible with MinVer.

### Gaps
- I couldn't confirm what MinVer 7.0 changed (release notes not fetched), the exact GitVersion version that shipped `ignore.paths` monorepo docs, or whether GitVersion's "ignore paths in git tree" feature landed.
- I couldn't confirm whether Nx release (`nx release`) supports versioning csproj/NuGet natively via `@nx/dotnet`. Search results were inconclusive.
- I didn't research Versionize (monorepo support status) or semantic-release .NET plugins (e.g. semantic-release-dotnet) this session. No verified facts.
- Maintenance status: NBGV is active (new `path-filters` commands documented). MinVer 7.0.0 is current. The changesets ports are all 0.x with low adoption.

## 2. Packaging: dotnet pack at scale, ProjectReference → PackageReference, package validation / API baselines, SourceLink, deterministic builds, symbols, feeds, trusted publishing

### Takeaway
At scale, the packaging baseline is:
- MSBuild-wide props: SourceLink, `ContinuousIntegrationBuild`/deterministic, `.snupkg` symbols, and package validation against a per-package baseline version.
- Supply-chain additions in 2025-26: Sigstore/cosign signing, GitHub attestations and SBOMs.
- NuGet.org trusted publishing (OIDC via `NuGet/login`), now adopted by OTel and Aspire. It supports GitHub Actions and GitLab, but not Azure Artifacts or private feeds.

### Cited Findings
- **NuGet.org trusted publishing:**
  - Your CI exchanges an OIDC token for a temporary API key valid **1 hour**, one token → one key.
  - GitHub policy fields: repo owner, repo, workflow *file name only*, optional environment.
  - **GitLab is also supported** via `id_tokens` with `aud: https://www.nuget.org` and a POST to `https://www.nuget.org/api/v2/token`. Its `ref` claim must be a branch, not a tag.
  - Policies can be scoped by glob to package IDs and actions, owned by a user or an org. Policies for private repos start "temporarily active for 7 days" until a publish locks in the repo/owner IDs (anti-resurrection).
  - Doc dated 2025-07-01, updated 2026-07-29 / 2026-09-01.
  - [Microsoft Learn: Trusted Publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing); [.NET blog announcement](https://devblogs.microsoft.com/dotnet/enhanced-security-is-here-with-the-new-trust-publishing-on-nuget-org/)
- OIDC trusted publishing works only for nuget.org, not Azure Artifacts or private feeds. Third-party skill doc, consistent with the official docs listing only nuget.org. — [skills.sh nuget-trusted-publishing](https://skills.sh/dotnet/skills/nuget-trusted-publishing)
- **Aspire's switch to trusted publishing (July 2026):**
  - Two workflows, `dotnet-main.yml` (beta, `nuget-beta` environment) and `dotnet-release.yml` (stable, `nuget-stable` environment), each with a policy matching repo + workflow file name.
  - The job uses `id-token: write`, `NuGet/login@v1` and `dotnet nuget push --skip-duplicate`, with a `NUGET_USER` secret per environment.
  - Common failures are configuration problems: missing id-token permission, wrong file name, wrong user.
  - [Aaron Powell, 2026-07-03](https://www.aaron-powell.com/posts/2026-07-03-switching-aspire-to-nuget-trusted-publishing/)
- **OpenTelemetry contrib publish pipeline:**
  - **Tag triggers** cover component prefixes (`Exporter.*-*`, `Instrumentation.*-*`, `Sampler.*-*`, `SemanticConventions-*`, …), plus a nightly cron and workflow_dispatch.
  - **Tag → project resolution:** `ResolveProjectForTag` (pwsh) resolves the tag to the full solution `.proj` (manual/nightly builds), a multi-package `.proj` in `build/Projects/` whose MinVerTagPrefix matches, or a generic `Component.proj` driven by a `BUILD_COMPONENT` env var.
  - **Steps:** build/test → cosign keyless-sign the DLLs → `actions/attest` for DLLs → `dotnet pack --no-build -p:PackTag=$TAG` → attest the nupkgs → attest the SBOM.
  - **Separate verify job:** checks the cosign signatures and attestations before any push.
  - **Push targets:** MyGet for nightlies, and nuget.org (tag only) via `NuGet/login@v1.2.0` with `id-token: write` and `--skip-duplicate`. Both are guarded so forks without secrets skip the push.
  - [contrib publish-packages.yml](https://github.com/open-telemetry/opentelemetry-dotnet-contrib/blob/main/.github/workflows/publish-packages.yml)
- **OTel core** uses the same cosign + attestation + SBOM pattern, with nightlies to MyGet. — [otel-dotnet publish-packages-1.0.yml](https://github.com/open-telemetry/opentelemetry-dotnet/blob/main/.github/workflows/publish-packages-1.0.yml)
- **OTel contrib `build/Common.prod.props`:**
  - **MinVer:** `PackageReference MinVer PrivateAssets=All` (disabled when `IntegrationBuild=true`). An `AssemblyVersionTarget` runs AfterTargets=MinVer and sets Assembly/File/Version to `Major.Minor.Patch.$(RevisionNumber)`.
  - **Package validation:**
    - `EnablePackageValidation` defaults false, but each csproj sets `PackageValidationBaselineVersion` (e.g. Instrumentation.Http baseline `1.20.1`).
    - An `EnsurePackageValidationBaselineVersion` target **errors** if validation runs without a baseline, unless `DisablePackageBaselineValidation` is set.
    - Validation is auto-disabled when MinVer yields `0.0.0-alpha.0` (forks without tags).
  - **Symbols and SourceLink:** `IncludeSymbols=true` and `SymbolPackageFormat=snupkg`, with `SourceRoot` set when `Deterministic`.
  - **Public API tracking:** PublicApiAnalyzers files under `.publicApi/<TFM>/PublicAPI.*.txt`.
  - **Readme links:** a pack target rewrites relative README links to GitHub permalinks using `git rev-parse HEAD`.
  - [contrib build/Common.prod.props](https://github.com/open-telemetry/opentelemetry-dotnet-contrib/blob/main/build/Common.prod.props); [Instrumentation.Http csproj](https://github.com/open-telemetry/opentelemetry-dotnet-contrib/blob/main/src/OpenTelemetry.Instrumentation.Http/OpenTelemetry.Instrumentation.Http.csproj)
- **Azure SDK for .NET `eng/Directory.Build.Common.props`:**
  - `EnableSourceLink` defaults true. `ContinuousIntegrationBuild=true` is set when `TF_BUILD` (Azure Pipelines) is true for shipping client libraries.
  - API compatibility uses its own `ApiCompatVersion` per csproj, plus `ApiCompatStandardTargetFramework=netstandard2.0` and `ApiCompatRunnableTargetFramework=net8.0`, with baselines under `eng/apicompatbaselines`.
  - `GenerateAPIListing` (API surface text files) is applied when `IsShippingClientLibrary`, along with banned-API analyzers and client-SDK analyzers. There is a `centralpackagemanagement` folder (CPM).
  - [eng/Directory.Build.Common.props](https://github.com/Azure/azure-sdk-for-net/blob/main/eng/Directory.Build.Common.props); [eng/ listing](https://github.com/Azure/azure-sdk-for-net/tree/main/eng)
- **Azure SDK reference mix:** a library references *within-service* siblings by `ProjectReference` (Blobs → `Azure.Storage.Common.csproj`) and cross-service core libraries by `PackageReference` (`Azure.Core`). `dotnet pack` turns the ProjectReference into a NuGet dependency at the sibling's current version. — [Azure.Storage.Blobs.csproj](https://github.com/Azure/azure-sdk-for-net/blob/main/sdk/storage/Azure.Storage.Blobs/src/Azure.Storage.Blobs.csproj)
- **Polly package validation:** the package-validation stage runs `meziantou.validate-nuget-package` (Meziantou.Framework.NuGetPackageValidation.Tool) and `dotnet-validate`, with tool versions pinned in `.config/dotnet-tools.json`. — [Polly build.yml](https://github.com/App-vNext/Polly/blob/main/.github/workflows/build.yml)
- **Polly signing:** a separate Windows sign job uses `azure/login` (OIDC) and `sign code azure-key-vault` (the dotnet/sign tool with a Key Vault certificate) under a GitHub environment, followed by `actions/attest`. Mutation testing is configured via `eng/stryker-config.json`. — [Polly build.yml](https://github.com/App-vNext/Polly/blob/main/.github/workflows/build.yml); [Polly eng/](https://github.com/App-vNext/Polly/tree/main/eng)

### Inferences
- **Converged 2026 "secure publish" pipeline:** build once → sign → attest → pack `--no-build` → verify in a separate job → push with an OIDC-minted key and `--skip-duplicate` (makes reruns idempotent). A monorepo tool should emit this shape per release unit.
- **Package-validation baselines** need per-package state (the last shipped version). OTel stores it in each csproj, Azure SDK in `ApiCompatVersion` updated by scripts. A tool that knows each package's last released version can manage these baselines automatically.
- **Trusted-publishing policies** bind to a *workflow file name*. A monorepo should centralize publishing in one (or few) workflow files to keep the number of nuget.org policies manageable. Org-owned policies with package-ID globs help.
- **Azure Artifacts / GitHub Packages** still need PATs/`GITHUB_TOKEN`/service connections. OIDC trusted publishing is nuget.org-only.

### Gaps
- I didn't fetch the official docs for `EnablePackageValidation`/`PackageValidationBaselineVersion`, SourceLink (now built into the .NET 8+ SDK), or Azure Artifacts/GitHub Packages push auth this session. The repo usage above is verified; SDK defaults are not.
- I didn't verify whether NuGet trusted publishing added Azure DevOps Pipelines as an OIDC provider by Oct 2026. The official page lists GitHub Actions and GitLab only.
- I didn't research NuGet's ProjectReference → PackageReference edge cases (e.g. `PrivateAssets`, `IncludeReferencedProjects`, non-packable referenced projects) from primary docs.

## 3. Release of deployables: container images, per-service deploys, Aspire deploy

### Takeaway
The .NET SDK builds OCI images with no Dockerfile and no Docker daemon. You run `dotnet publish /t:PublishContainer` and either push straight to a registry (`ContainerRegistry`) or write a tarball (`ContainerArchiveOutputPath`). That makes per-service image release a pure MSBuild/CLI operation a monorepo tool can drive per project. Aspire-specific deploy flows (`aspire publish/deploy`, `azd`) weren't verified this session.

### Cited Findings
- **`dotnet publish --os linux --arch x64 /t:PublishContainer`** builds an image (needs the .NET 8+ SDK).
  - The image name defaults to `AssemblyName`, overridable via `ContainerRepository`.
  - By default the image is pushed to the local Docker/Podman daemon.
  - `-p ContainerArchiveOutputPath=./images/x.tar.gz` writes a tarball with no daemon needed (useful for security scanning).
  - `-p ContainerRegistry=ghcr.io` pushes straight to a registry "without using any container runtime at all".
  - Doc dated 2026-05-27.
  - [Microsoft Learn: Containerize an app with dotnet publish](https://learn.microsoft.com/dotnet/core/containers/sdk-publish)
- Aspire's own repo ships the CLI through winget, Homebrew and nix packaging (`eng/winget`, `eng/homebrew`, `eng/nix`), validated by CI gates `run_winget_installer`, `run_homebrew_installer`, `run_nix_package`. — [microsoft/aspire tests.yml](https://github.com/microsoft/aspire/blob/main/.github/workflows/tests.yml); [eng/](https://github.com/microsoft/aspire/tree/main/eng)

### Inferences
- A monorepo tool can treat "deployable" projects as release units whose artifact is an image. The version comes from the same versioning source (MinVer/NBGV → `ContainerImageTags`). The deploy is a downstream, environment-gated job keyed on the affected set.

### Gaps
- I didn't fetch docs for `ContainerImageTags`, `ContainerRuntimeIdentifiers` (multi-arch), `ContainerFamily` or `EnableSdkContainerSupport` requirements. The doc page fetched covered only the basics.
- I couldn't verify the current Aspire deployment commands (`aspire publish`, `aspire deploy`, azd integration) as of Oct 2026. Not researched due to budget, and the report writer should not state specifics without a source.

## 4. CI patterns for .NET monorepos: path filters, dynamic matrices, ADO path triggers/templates, sharding, caching, required checks

### Takeaway
Real .NET monorepos use three patterns:
1. A "detect changes" job, either path-filter globs (OTel) or an MSBuild-project-graph-based selector (Aspire's SelectTests).
2. Conditional per-component reusable workflows or dynamic matrices.
3. An always-running aggregator job, which is the only required check. This works around GitHub's rule that path-skipped workflows leave required checks Pending.

Azure SDK for .NET instead uses one Azure DevOps `ci.yml` per service directory with `trigger.paths` include/exclude. All of them extend a shared archetype template, and a single PR pipeline auto-detects the affected service (`Service: auto`). It also has weighted test batching.

### Cited Findings
- **GitHub required-checks semantics:**
  - A job skipped by a conditional `if:` "reports Success".
  - Workflows skipped by path, branch or commit-message filtering leave "Associated checks … in a 'Pending' state and block merging". GitHub recommends against making such skippable workflows required.
  - If a check and a commit status share a name, both must pass.
  - [GitHub Docs: troubleshooting required status checks](https://docs.github.com/en/pull-requests/collaborating-with-pull-requests/collaborating-on-repositories-with-code-quality-features/troubleshooting-required-status-checks)
- **opentelemetry-dotnet-contrib `ci.yml`, the canonical GH Actions monorepo pattern:**
  - **Detect changes:** a `detect-changes` job uses `AurorNZ/paths-filter@v5.1.0` (pinned by SHA) with named filters:
    - `md`, `pwsh`, `yml`
    - `build` (`build/**`, `.github/**/*.yml`, `**/*.targets`, `**/*.props`, `global.json`, `NuGet.config`)
    - `shared` (`src/Shared/**`, `test/Shared/**`)
    - `code`
    - one per component (e.g. `exporter-geneva: ['*/OpenTelemetry.Exporter.Geneva*/**', '!**/*.md']`)
  - **Per-component jobs:** each `build-test-<component>` job calls the reusable `Component.BuildTest.yml`, gated by `if: contains(needs.detect-changes.outputs.changes, '<component>') || contains(..., 'build') || contains(..., 'shared')`. Shared infra changes fan out to everything.
  - **Aggregator:** a final job `needs:` every job, uses `if: always()`, and fails when `contains(needs.*.result, 'failure') || contains(needs.*.result, 'cancelled')`. Skipped jobs count as OK. This makes one stable required check.
  - **Merge queue:** checkout runs on `merge_group` events too.
  - [contrib ci.yml](https://github.com/open-telemetry/opentelemetry-dotnet-contrib/blob/main/.github/workflows/ci.yml)
- **Aspire (`microsoft/aspire`) selective CI, the most sophisticated affected-detection found:**
  - **The selector:** a `./.github/actions/select-tests` action runs a SelectTests tool. "Layer 1" is an MSBuild `ProjectGraph` over `Aspire.slnx`. "Layer 2" is a curated trigger map (`eng/github-ci/test-trigger-map.yml`) for non-.NET jobs (TypeScript SDK, VS Code extension, installers).
  - **Restriction props:** in enforce mode it writes an `OverrideProjectToBuild` props file, consumed via `$(BeforeBuildPropsPath)` by `eng/Build.props`. The enumerate-tests step then builds only the selected projects.
  - **Audit vs enforce:** in audit mode it runs everything but reports what it would have skipped.
  - **Diff base:** it diffs against the PR's real head, not the synthetic merge ref, to avoid over-selection when the base advances.
  - **Explainability:** a `run-full-ci` PR label is a kill switch. It writes a selection-summary PR comment (posted from a separate least-privilege job) and a JSON record of each selection's cause.
  - **Matrix split:** test matrices are split by requirement (`tests_matrix_no_nugets`, `tests_matrix_requires_nugets_linux/windows/macos`, `tests_matrix_requires_cli_archive`) using test "runsheet builders" in `eng/` (`TestEnumerationRunsheetBuilder`, `OuterloopTestRunsheetBuilder`, `QuarantinedTestRunsheetBuilder`), plus `eng/test-retry-patterns.json` for retries.
  - **Aggregator:** a final `ci_green`-style job aggregates `needs.*.result`.
  - [aspire tests.yml](https://github.com/microsoft/aspire/blob/main/.github/workflows/tests.yml); [aspire select-tests action.yml](https://github.com/microsoft/aspire/blob/main/.github/actions/select-tests/action.yml); [aspire ci.yml](https://github.com/microsoft/aspire/blob/main/.github/workflows/ci.yml); [aspire eng/](https://github.com/microsoft/aspire/tree/main/eng)
- **Azure SDK for .NET (Azure DevOps):**
  - **Per-service `ci.yml`:** each `sdk/<service>/ci.yml` declares `trigger.branches` (main, hotfix/*, release/*) and `trigger.paths.include/exclude`. For example, storage includes `sdk/storage/` but excludes `Azure.ResourceManager.Storage/` and the WebJobs extension dirs, which have their own pipelines.
  - **Shared template:** each one `extends: template: /eng/pipelines/templates/stages/archetype-sdk-client.yml` with `ServiceDirectory` and an `Artifacts:` list (name, safeName, skipSmokeTests) that defines the release units. A `ReleaseToDevOpsOnly` runtime parameter redirects the release to a DevOps feed.
  - **PR pipeline:** a single `eng/pipelines/pullrequest.yml` (pr trigger on main/feature/hotfix/release/sdkauto branches) extends the same archetype with `Service: auto`, meaning the affected service directory is computed from the PR diff.
  - **1ES templates:** a `oneESTemplateTag` parameter selects release/canary (Microsoft 1ES Pipeline Templates).
  - [sdk/storage/ci.yml](https://github.com/Azure/azure-sdk-for-net/blob/main/sdk/storage/ci.yml); [eng/pipelines/pullrequest.yml](https://github.com/Azure/azure-sdk-for-net/blob/main/eng/pipelines/pullrequest.yml)
- **Azure SDK test-sharding and dependency scripts:** `Apply-WeightedBatching.ps1`, `Get-TestAssemblyWeights.ps1` and `Get-PackageLocWeights.ps1` balance test batches by weight. There is also a `splittestdependencies` folder and `Validate-CpmCompliance.ps1`. — [eng/scripts listing](https://github.com/Azure/azure-sdk-for-net/tree/main/eng/scripts)
- **Polly CI:** an OS matrix (`matrix.runner`/`os-name`) with a single `./build.ps1` entry (Cake via `cake.cs`). It sets `NUGET_PACKAGES` to a fixed path for caching (`D:\.nuget\packages` on Windows) and uploads per-OS Codecov flags. — [Polly build.yml](https://github.com/App-vNext/Polly/blob/main/.github/workflows/build.yml)
- **Supply-chain hygiene:** all three GitHub repos pin every action by commit SHA with a version comment (e.g. `actions/checkout@3d3c42e… # v7.0.1`, `actions/setup-dotnet@a98b568… # v6.0.0`). OTel also runs zizmor (workflow security linting), actionlint and OSSF Scorecard. — [contrib workflows dir](https://github.com/open-telemetry/opentelemetry-dotnet-contrib/tree/main/.github/workflows); [contrib publish-packages.yml](https://github.com/open-telemetry/opentelemetry-dotnet-contrib/blob/main/.github/workflows/publish-packages.yml)
- **Runner images:** OTel uses `windows-2025` and `ubuntu-26.04` runner labels. — [contrib publish-packages.yml](https://github.com/open-telemetry/opentelemetry-dotnet-contrib/blob/main/.github/workflows/publish-packages.yml)

### Inferences
- **The "required check + path filter" problem has one accepted solution:** never put `paths:` on a required workflow. Always run a cheap detect job, gate the expensive jobs with `if:` (skipped = success), and require only an `if: always()` aggregator. A monorepo tool should generate this structure, or a single dynamic-matrix job plus an aggregator.
- **Path-glob filters vs graph-based selection:** glob filters (OTel) are simple but hand-maintained and coarse; any `*.props` change runs everything. Aspire's graph-based selection (MSBuild ProjectGraph + trigger map for non-MSBuild assets + audit mode + explainability + kill-switch label) is the state-of-the-art blueprint. It is essentially what a new .NET monorepo tool's "affected" command should productize. `dotnet-affected` is an existing OSS building block.
- **Azure DevOps scale pattern:** one pipeline definition per "release unit group" (service directory), each extending a shared stage template. That gives per-service path triggers, release stages and artifact lists. On GitHub Actions the equivalent is reusable workflows.

### Gaps
- I didn't fetch primary docs for `dorny/paths-filter` (maintenance status in 2026), `actions/setup-dotnet` `cache: true` with `packages.lock.json`, GitHub merge-queue docs, Azure DevOps path-trigger docs, GitLab `rules:changes`, or TeamCity monorepo support. I also didn't verify the provenance of `AurorNZ/paths-filter` (likely a fork of dorny/paths-filter, unverified).
- I didn't find evidence of MSBuild build-result caching in CI (remote cache) in any of these repos. They rely on NuGet package caching and selective building, not output caching. I didn't research third-party remote caches (e.g. Nx Cloud with `@nx/dotnet`, or MSBuild cache plugins / "MSBuildCache" from Microsoft) this session.
- CODEOWNERS: OTel contrib has no `.github/CODEOWNERS` (404). It appears to rely on `assign-reviewers.yml`/`add-labels.yml` workflows with component-owner config, but I didn't verify the config file. I didn't research CODEOWNERS for Azure SDK (known to be very large; not verified).
- I didn't research AWS SDK for .NET, Akka.NET, MassTransit, Orleans, Avalonia or Microsoft.Extensions (dotnet/runtime, dotnet/extensions) this session because of the tool-call budget.

## 5. Real public .NET repos with multi-package release automation (summary table, verified 2026-10-28)

### Takeaway
There are three archetypes:
- Tag-prefix independent releases with MinVer (OTel contrib) or tag-prefix lockstep groups (OTel core).
- Script-managed csproj `<Version>` with per-service ADO pipelines and API-compat baselines (Azure SDK).
- Arcade / central Versions.props lockstep with graph-based selective CI (Aspire), plus a simple single-version Cake build (Polly).

### Cited Findings
- **opentelemetry-dotnet-contrib** (independent):
  - **Versioning:** per-csproj `MinVerTagPrefix` (e.g. `Instrumentation.Http-`), tags `<Component>-<semver>`.
  - **Releases:** started with a `prepare-release.yml` dispatch (component + version), which opens a PR and later a tag.
  - **Publish:** the tag triggers `publish-packages.yml`, which resolves the tag to a project via `ResolveProjectForTag`, then signs, attests, packs, verifies, and pushes to nuget.org via trusted publishing. Nightlies go to MyGet.
  - **Validation:** per-package `PackageValidationBaselineVersion`.
  - **CI:** path-filter detect job, per-component reusable workflow, aggregator required check, merge_group support.
  - [publish-packages.yml](https://github.com/open-telemetry/opentelemetry-dotnet-contrib/blob/main/.github/workflows/publish-packages.yml); [ci.yml](https://github.com/open-telemetry/opentelemetry-dotnet-contrib/blob/main/.github/workflows/ci.yml); [Common.prod.props](https://github.com/open-telemetry/opentelemetry-dotnet-contrib/blob/main/build/Common.prod.props)
- **opentelemetry-dotnet** (lockstep groups):
  - **Versioning:** tag prefixes `core-`, `coreunstable-`, `declarativeconfig-` shared across groups of csprojs, with MinVer.
  - **Experimental APIs:** `ExposeExperimentalFeatures` is toggled by prerelease status.
  - **Publish:** the same sign/attest/SBOM pipeline. The workflow file name is deliberately frozen ("DON'T RENAME THIS FILE UNLESS WE START RELEASING THE VERSION 2.*"), consistent with trusted-publishing policies keyed on the workflow file name.
  - [publish-packages-1.0.yml](https://github.com/open-telemetry/opentelemetry-dotnet/blob/main/.github/workflows/publish-packages-1.0.yml); [Common.prod.props](https://github.com/open-telemetry/opentelemetry-dotnet/blob/main/build/Common.prod.props)
- **Azure/azure-sdk-for-net** (independent, hundreds of packages):
  - **Versioning:** csproj `<Version>` + `<ApiCompatVersion>`, edited by `eng/scripts/Update-PkgVersion.ps1` / `Prepare-Release-NonInteractive.ps1`. Dev builds get `-alpha.yyyyMMdd.r` (`eng/Versioning.targets`).
  - **Pipelines:** a per-service ADO `ci.yml` with path include/exclude extends `archetype-sdk-client.yml` and lists its release `Artifacts`. The PR pipeline auto-detects the service.
  - **Build infra:** weighted test batching, CPM, API listing/compat baselines, 1ES templates.
  - [sdk/storage/ci.yml](https://github.com/Azure/azure-sdk-for-net/blob/main/sdk/storage/ci.yml); [eng/Versioning.targets](https://github.com/Azure/azure-sdk-for-net/blob/main/eng/Versioning.targets); [pullrequest.yml](https://github.com/Azure/azure-sdk-for-net/blob/main/eng/pipelines/pullrequest.yml)
- **microsoft/aspire** (lockstep):
  - **Versioning:** Arcade-style `eng/Versions.props` (Major/Minor/Patch + PreReleaseVersionLabel + StabilizePackageVersion) and `eng/Version.Details.xml`.
  - **CI:** graph-based selective CI (SelectTests) with split matrices and runsheet builders.
  - **Publish:** NuGet trusted publishing in GitHub Actions per the July 2026 blog. Note: the blog names workflows `dotnet-main.yml`/`dotnet-release.yml`. I didn't confirm these exist in microsoft/aspire, and the post may describe a different Aspire-related repo (e.g. Aspire community toolkit).
  - [Versions.props](https://github.com/microsoft/aspire/blob/main/eng/Versions.props); [tests.yml](https://github.com/microsoft/aspire/blob/main/.github/workflows/tests.yml); [Aaron Powell blog](https://www.aaron-powell.com/posts/2026-07-03-switching-aspire-to-nuget-trusted-publishing/)
- **App-vNext/Polly** (lockstep):
  - **Build:** one version for all packages, any-tag trigger, a Cake build (`build.ps1` + `cake.cs`) run in an OS matrix.
  - **Validation and signing:** package-validation tools, Azure Key Vault signing via the `sign` tool with OIDC `azure/login`, and attestations.
  - **Version and baselines:** `eng/bump-version.ps1`, `update-baselines.ps1`, `update-changelog.ps1`.
  - [build.yml](https://github.com/App-vNext/Polly/blob/main/.github/workflows/build.yml); [eng/](https://github.com/App-vNext/Polly/tree/main/eng)

### Inferences
- Every large repo built bespoke glue (PowerShell modules, MSBuild targets, ADO templates) for exactly the functions a monorepo tool would provide:
  - tag → project resolution
  - release-unit definitions (Artifacts lists, `build/Projects/*.proj`)
  - per-package baseline management
  - affected detection
  - aggregated required checks
- That is the clearest evidence of unmet demand.
- A new tool should be MSBuild-native (props/targets + ProjectGraph) rather than forcing a JS toolchain (Nx/changesets/release-please). It should interoperate with MinVer-style tag prefixes and existing csproj `<Version>` fields, not replace them.

### Gaps
- I didn't examine AWS SDK for .NET (generator-driven, per-service NuGet packages with a central version manifest — not verified), Akka.NET, MassTransit, Orleans, Avalonia or dotnet/extensions this session. Their approaches are unconfirmed.
- I didn't confirm Azure SDK's exact release-stage mechanics (manual approval stage per artifact, docs/release-tagging steps inside `archetype-sdk-client.yml`) because I didn't read that template.
