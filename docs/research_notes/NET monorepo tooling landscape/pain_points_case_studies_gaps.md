# .NET Monorepos: Community Pain Points, Case Studies, Ecosystem Gaps (as of late Oct 2026)

Research note scope: what .NET developers/orgs struggle with in monorepos, how notable orgs run large .NET (or Microsoft-scale) monorepos, IDE behavior at scale, commercial offerings, and gaps/opportunities for a new .NET monorepo tool.

Methodology caveat (important for the report writer): Reddit (r/dotnet, r/csharp, r/ExperiencedDevs) could not be searched or fetched. The search tool returned "domains not accessible" for reddit.com, and general web search surfaced no Reddit threads. Community sentiment below therefore comes from Hacker News (via the hn.algolia.com API), GitHub issues/discussions, JetBrains YouTrack, Microsoft devblogs, and individual engineering blogs. Reddit-specific quotes are an explicit gap.

## 1. Community discussions: recurring complaints about .NET monorepos

### Takeaway
The complaints that keep coming up are: (a) CI rebuilds and retests everything because nothing tells it what changed; (b) MSBuild has no built-in cloud/remote cache, and outside Microsoft the only plugin route is experimental; (c) solution/project files cause merge conflicts and churn (partly fixed by .slnx, which became the `dotnet new sln` default in .NET 10); (d) NuGet version governance gets complicated under Central Package Management (transitive pinning, VersionOverride, NU1109); (e) IDE memory and hangs on large solutions. The problems are real but the threads are low-engagement. Single-digit vote counts on GitHub are typical, so the demand is spread thin rather than concentrated in one place people rally around.

### Cited Findings
**Affected detection / CI scope**
- Francis Phan (Triage-Plus, a 97% C# codebase), Jan 21 2024: "a single change would force us to test and deploy all the microservices, even if they were not affected by the change." The team decided its real problem was managing the project dependency DAG, not compile speed. They used `dotnet-affected` to emit `affected.txt`/`affected.proj` and test/deploy only those. Gotcha: "GHA only shallow clones the repository," so `fetch-depth: 0` is needed. Advice: "Adopt the monorepo pattern first, and then adopt a monorepo tool as you scale." He was wary of Bazel/Nx because they "wrap around the dotnet CLI" and risk losing IDE support, especially debugging in Rider. — [phan.nz](https://www.phan.nz/posts/doing-monorepo-dotnet/)
- The same author argues .NET is "naturally" monorepo-shaped, since multi-project solutions are native to .NET. — [phan.nz](https://www.phan.nz/posts/doing-monorepo-dotnet/)
- HN, 2019-01-24 (CuriousSkeptic), on moving a .NET monorepo from AppVeyor to Azure DevOps: required that "only impacted components should build" and wanted to run and debug pipeline steps locally. — [HN item 18991375](https://news.ycombinator.com/item?id=18991375)
- `dotnet-affected` (leonardochaia, MIT, ~340 stars per third-party trackers) finds all .csproj/.fsproj/.vbproj files, builds an MSBuild `ProjectGraph`, marks changed projects plus their dependents, and emits a Microsoft.Build.Traversal project for `dotnet build/test`. It supports CPM, regex exclusions, and an MSBuild SDK package (DotnetAffected.Tasks). — [ecosyste.ms summary](https://awesome.ecosyste.ms/projects/github.com%2Fleonardochaia%2Fdotnet-affected); [DotnetAffected.Tasks NuGet](https://packages.nuget.org/packages/DotnetAffected.Tasks)
- Other small affected-detection tools exist on NuGet: Petabridge Incrementalist, `dotnet-monorepo` (0.0.x, git-diff affected targeting), DotMR.Cli (0.0.13, "in active development"), JustBuild. All are niche and early. — [Incrementalist](https://awesome.ecosyste.ms/projects/github.com%2Fpetabridge%2Fincrementalist); [dotnet-monorepo](https://packages.nuget.org/packages/dotnet-monorepo); [DotMR.Cli](https://www.nuget.org/packages/dotmr.cli); [JustBuild](https://www.nuget.org/packages/justbuild)

**Remote/distributed build caching**
- dotnet/msbuild Discussion #8718 "Distributed Build Options" (May 2 2023, 5 votes). The poster's monorepo build times keep growing, and "msbuild doesn't provide its own cloud backed distributed cache solution." Git-history project selection only helps so much. They had looked at a custom MSBuild task (hard to maintain), nx-dotnet (worried about custom props/targets), Docker builds, and BuildXL ("little public documentation or support"). Microsoft's JanKrivanek answered (Jan 3 2024) that "There is currently support for pluging-in 3rd pty cache providers," with docs still pending. dfederm (Microsoft) pointed to microsoft/MSBuildCache as "out-of-box plugins." The poster had not known it existed and had hit issues trying the plugin locally (#9441). Engagement was very low: 3 participants. — [GitHub discussion #8718](https://github.com/dotnet/msbuild/discussions/8718)
- dotnet/msbuild #9122 (closed) asked MSBuild to expose the hash it uses to decide whether a project should build, explicitly so third parties could build distributed caching on top. #6182 (backlog) asks for machine-agnostic evaluation results so the project cache can move to the cloud or the repo. — [msbuild #9122](https://github.com/dotnet/msbuild/issues/9122); [msbuild #6182](https://github.com/dotnet/msbuild/issues/6182)
- microsoft/MSBuildCache #104: a user expected deterministic, source-hash-based skipping. The maintainer explained the cache *replays file writes per project* rather than snapshotting disk. Users don't understand the cache model well. — [MSBuildCache #104](https://github.com/microsoft/MSBuildCache/issues/104)

**Solution / project file churn**
- Milan Jovanovic's migration guide says .sln GUID-heavy entries cause frequent merge conflicts, and that this hurts most in "large monorepo solutions with hundreds of projects." `dotnet sln migrate` (SDK 9.0.200+) generates .slnx. — [milanjovanovic.tech](https://milanjovanovic.tech/blog/the-new-slnx-solution-format-migration-guide)
- .NET 10 breaking change: from .NET 10 RC1, `dotnet new sln` creates .slnx by default. Opt out with `--format sln`. — [Microsoft Learn breaking change](https://learn.microsoft.com/en-us/dotnet/core/compatibility/sdk/10.0/dotnet-new-sln-slnx-default)
- Historical (2014): .csproj merge conflicts were bad enough that GitHub for Windows briefly defaulted `*.csproj merge=union`, which caused its own problems. — [haacked.com (2014)](https://haacked.com/archive/2014/04/16/csproj-merge-conflicts/)
- Tools exist to stitch multi-repo solutions together (dotnet-slnmerge swaps PackageReference for ProjectReference across solutions). This shows demand for "composite solution" workflows. — [dotnet-slnmerge](https://github.com/ahelper/dotnet-slnmerge)

**NuGet version drift / CPM governance**
- NuGet CPM (introduced Apr 12 2022) lets `VersionOverride` override central versions per PackageReference. — [NuGet blog](https://devblogs.microsoft.com/nuget/introducing-central-package-management/)
- With transitive pinning, you cannot pin a package below what a dependency requests; restore fails with NU1109. Microsoft says transitive pinning "should be carefully evaluated when authoring a library." — [NU1109 docs](https://learn.microsoft.com/en-gb/nuget/reference/errors-and-warnings/nu1109); [CPM docs](https://learn.microsoft.com/en-us/nuget/consume-packages/central-package-management)
- Floating versions are disallowed in PackageVersion by default (NU1011). — [NU1011](https://learn.microsoft.com/nuget/reference/errors-and-warnings/nu1011)
- Real example: dotnet/fsharp PR #20084 (merged Aug 2026) turned on CPM with transitive pinning across the repo and had to "raise the central pins to those floors so transitive pinning does not downgrade them," keeping some versions unpinned for source-build. — [dotnet/fsharp #20084](https://github.com/dotnet/fsharp/pull/20084)
- G-Research wrote publicly about centrally managing NuGet versions (CPVM, the CPM precursor), an early example of a large .NET shop needing version governance. — [G-Research](https://www.gresearch.com/news/centrally-managing-nuget-package-versions-cpvm/)

**Monorepo vs. polyrepo sentiment (HN)**
- 2025-05-28: in a large monorepo, upgrading .NET "requir[es] the architecture team and everything stays old," which blocks teams from adopting newer tools. Lockstep framework upgrades are a pain point. — [HN 44113973](https://news.ycombinator.com/item?id=44113973)
- 2024-03-18: cross-repo tooling is poor and "most tooling only works well for monorepos." Cites the .NET team folding its repos into a monorepo. — [HN 39750958](https://news.ycombinator.com/item?id=39750958)
- 2023-03-22: .NET has used Gradle-style multi-project monorepos for about 20 years, so the layout is normal. — [HN 35255158](https://news.ycombinator.com/item?id=35255158)
- 2024-10-24: one developer runs a ~100k LOC C# + frontend monorepo in VS Code on a 16 GB MacBook without issues. Small/medium .NET monorepos are fine without special tooling. — [HN 41936489](https://news.ycombinator.com/item?id=41936489)

**Tooling performance**
- dotnet/format PR #1485: `.editorconfig` discovery was slowed by a huge `node_modules` folder inside the solution. Analyzers are slow because they need a build. A workspace cache was discussed. Mixed JS+.NET monorepos hurt .NET tooling. — [dotnet/format #1485](https://github.com/dotnet/format/pull/1485)

### Inferences
- The most common pain is "CI does too much." People solve it with home-grown git-diff scripts or dotnet-affected. Remote caching comes second, and people only ask for it once affected detection stops being enough (#8718 says this outright).
- GitHub engagement on caching is very low (5 votes on #8718; MSBuildCache unknown to a motivated user). The demand is latent and badly served: people don't know options exist, rather than not wanting them.
- .slnx fixes the GUID/merge-conflict class of problems at the format level, so "solution management" is moving away from conflict resolution toward generating and keeping in sync solutions/filters that cover subsets of projects.
- CPM is now the default way to govern versions, but it leaves edge-case complexity (pinning floors, overrides, source-build exceptions) with no auditing or policy tooling.

### Gaps
- No Reddit threads could be retrieved (domain blocked), so there are no r/dotnet/r/csharp/r/ExperiencedDevs quotes or upvote counts.
- No Stack Overflow question view counts were collected.
- No quantified survey (e.g., JetBrains Developer Ecosystem or Stack Overflow survey) on .NET monorepo prevalence was found.

## 2. Case studies: how notable organizations run large .NET / Microsoft-scale monorepos

### Takeaway
Microsoft is the main source of public .NET-at-scale evidence. The Windows repo (~300 GB, ~3.5M files, 2017) needed GVFS. Office needed Scalar plus sparse checkout. .NET itself spent nearly four years (2022 to .NET 10 RTM, Nov 2025) on a "Virtual Monolithic Repo" (dotnet/dotnet) with two-way code flow, cutting a ~24-hour product build toward a goal of under 4 hours unsigned / under 7 hours signed. Microsoft's own conclusion is that a monorepo for building, plus polyrepos for development, is costly and complex. Public non-Microsoft case studies of large .NET monorepos (or .NET-to-Bazel/Nx migrations) are scarce.

### Cited Findings
**dotnet/dotnet VMR and Unified Build**
- "Reinventing how .NET builds and ships (again)," Matt Mitchell, Nov 20 2025. .NET began split across repos (CoreCLR, CoreFX, Core-Setup). One security fix took "at least 36 hours" before handoff to validation. A .NET 8 runtime build: total 2 days 02:18:10, overhead including queueing 38.5%, longest path 5h40m. A full .NET 8/9 build "can easily run to 24" hours. Source Build (single platform, one machine) took ~50 min. Unified Build design started early 2022 (.NET 7), was targeted at .NET 9, slipped to .NET 10, and shipped at .NET 10 RTM after "almost 4 years." The product is built as 35–40 "verticals" (single command, single machine) plus "joins" for cross-platform pieces. Goals: unsigned build under 4h, signed under 7h. Code flow uses last-flow SHAs in `eng/Version.Details.xml` and Maestro. Future: AI agents to monitor code flow (.NET 11). — [.NET Blog](https://devblogs.microsoft.com/dotnet/reinventing-how-dotnet-builds-and-ships-again/)
- "How We Synchronize .NET's Virtual Monorepo," Přemek Vysoký, Jan 12 2026. The VMR aggregates several dozen product repos, with "millions of lines of code monthly across dozens of repositories" and hundreds of developers. Evolution: Source Build tarball (.NET 6), then VMR-lite read-only mirror (Oct 2022; release vehicle for Linux x64 source build from .NET 8 Preview 1), then a writable two-way VMR with "flat flow" (.NET 10 Preview 5). The SDK has been built from one VMR commit since .NET 10 Preview 4. They rejected git submodules/subtree/subrepo and use checked-in copies moved as `git diff --patch --binary` patches, split recursively above git's 1 GB limit, tracked in `source-manifest.json`. Problems: conflicts from interleaved forward/back flows even when nobody touched the same code (the manifest kept conflicting), and a "revert problem" that silently lost reverts. Fix (Dec 2025): rebase the PR branch; on conflict open an empty PR with a blocking status check and local tooling that auto-resolves known non-conflicts. Remaining issues: mismatched branch lifecycles, centrally snapped release branches, metadata corruption from merges (exploring git notes). — [.NET Blog](https://devblogs.microsoft.com/dotnet/how-we-synchronize-dotnets-virtual-monorepo/)
- Repo README: the VMR gives "the simplicity of a mono-repo for building and servicing the product" while active development stays in individual repos. — [github.com/dotnet/dotnet](https://github.com/dotnet/dotnet)
- Design doc: VMR-Design-And-Operation.md (dotnet/arcade Documentation/UnifiedBuild). — [mirror](https://source-hub.etos-pro.ru/dotnet/arcade/src/branch/main/Documentation/UnifiedBuild/VMR-Design-And-Operation.md)

**Windows / Office (Git at scale)**
- Brian Harry, May 24 2017, "The largest Git repo on the planet": Windows repo ~300 GB, ~3.5M files, ~4,000 engineers, ~1,700 daily builds across 440 branches, migrated from Source Depot (a Perforce derivative). A typical dev needs ~50–100K files, so GVFS virtualizes the filesystem. A later update reported 8,421 PRs and 1,760 official builds/day. — [Brian Harry devblog](https://devblogs.microsoft.com/bharry/the-largest-git-repo-on-the-planet); [InfoQ](https://www.infoq.com/news/2017/05/microsoft-windows-git); [The Register](https://www.theregister.co.uk/2017/05/25/windows_is_now_built_on_git)
- Office needed cross-platform support (macOS), so VFS for Git was replaced by Scalar plus sparse-checkout "cone mode." This works because Office's dependency system identifies which files a local build needs. Scalar shipped in core Git 2.38 (Oct 2022). microsoft/git still bundles it. — [GitHub blog "The Story of Scalar"](https://github.blog/open-source/git/the-story-of-scalar/); [microsoft/git](https://github.com/microsoft/git)
- Microsoft's 1JS (Office online JS monorepo) was shrunk by 94% (repo size), per a podcast summary. — [WeAreDevelopers](https://www.wearedevelopers.com/magazine/how-microsoft-avoided-a-git-limitation-to-shrink-a-repository-by-94-per-cent)

**BuildXL / MSBuildCache (Microsoft 1ES tech made public)**
- BuildXL is an open-source build engine that speeds up builds in several languages, including MSBuild. It is "not meant as a replacement for MSBuild." — [microsoft/BuildXL](https://github.com/Microsoft/BuildXL)
- microsoft/MSBuildCache (MIT) implements plugins for MSBuild's *experimental* project cache, using BuildXL's LocalCache, with Azure Blob Storage and Azure Pipelines backends. Packages are previews (e.g., 0.1.328-preview). The Azure Pipelines flavor "is expected" to run inside Azure Pipelines. Maintainers warn that builds using it must be limited to trusted team members, "Use at your own risk." — [microsoft/MSBuildCache](https://github.com/microsoft/MSBuildCache); [NuGet Microsoft.MSBuildCache.Common](https://www.nuget.org/packages/Microsoft.MSBuildCache.Common/)

**Stack Overflow**
- Nick Craver (InfoQ, Apr 2020) on the ASP.NET MVC to ASP.NET Core migration: "wherever we've gone outside the framework to extend, replace, hack... that's where pain usually lies." In 2018 he said the wins were maintenance, OSS fixes, SDK-style projects, and containers, not performance. No public detail on how SO's solution/repo is structured was found. — [InfoQ](https://www.infoq.com/news/2020/04/Stack-Overflow-New-Architecture); [wiert.me quoting tweet](https://wiert.me/2018/08/21/nick-craver-on-twitter-this-is-worth-repeating-were-migrating-stack-overflow-to-net-core-its-not-because-of-performance-there-are-enough-major-wins-without-even-factoring-performance-fo)

**Azure SDK for .NET**
- A large multi-package repo with its own engineering system in a dedicated Azure DevOps org (public/internal projects). Shared scripts live in azure-sdk-tools, and the EngSys team does releases. Package count wasn't verified; the latest-packages page lists them. — [CONTRIBUTING.md](https://github.com/Azure/azure-sdk-for-net/blob/main/CONTRIBUTING.md); [latest .NET packages](https://azure.github.io/azure-sdk/releases/latest/dotnet.html)

**Bazel for .NET (non-Microsoft)**
- rules_dotnet (bazel-contrib) docs assume the whole repo uses one framework version. Dependencies are copied side by side, and NuGet-heavy graphs are a complication. Issue #258 "Towards full build generation" (from a team with hundreds of polyrepos) asks for Gazelle-like BUILD generation. Hand-maintaining BUILD files is a known blocker. — [rules_dotnet runtime docs](https://docs-legacy.aspect.build/bazelbuild/rules_dotnet/0.0.7/docs/runtime); [rules_dotnet #258](https://github.com/bazelbuild/rules_dotnet/issues/258)
- No public engineering-blog case study of a company moving a C# monorepo to Bazel turned up. — (search: no results)

### Inferences
- Microsoft's internal answer (CloudBuild/BuildXL, VFS/Scalar, custom code-flow services) depends on large dedicated infra teams. The parts that are public (MSBuildCache, BuildXL, Scalar) are preview-grade or Azure-tied, which leaves room for a vendor-neutral, polished product.
- The VMR shows how expensive *hybrid* setups are (polyrepo dev with monorepo build). Bidirectional sync and conflict handling are a hard, unsolved product problem that Microsoft had to build its own tooling for.
- The lack of non-Microsoft .NET monorepo case studies is itself a signal. Large .NET shops (banks, G-Research, game studios) seldom publish, so an "evidence gap" exists that a vendor could fill with benchmarks.

### Gaps
- No primary data found for JetBrains' own Rider/ReSharper monorepo, Jet.com/Walmart, Bing, Unity, or Uber .NET usage. These were not researched due to the call budget.
- CloudBuild scale numbers (cache hit rates, build counts) were not retrieved. A Aug 2021 Engineering@Microsoft post surfaced in search ([link](https://devblogs.microsoft.com/engineering-at-microsoft/?p=134)) but wasn't read.
- The Microsoft ISE post ".NET multi-repo" (Aug 15 2023) surfaced but wasn't read: [devblogs ISE](https://devblogs.microsoft.com/ise/dotnet-multi-repo).
- The figure "24h to under 7h" for the VMR build is a *goal/target* in Microsoft's post, not a verified achieved number.
- Precise dotnet/runtime size/LOC was not gathered.

## 3. IDE experience at scale (VS 2022/2026, Rider, VS Code C# Dev Kit)

### Takeaway
All three IDEs have historically struggled with 100–400+ project solutions (memory blowups, multi-GB backends, UI hangs). In 2025–2026 each vendor shipped major large-solution work: VS 2026 (GA Nov 2025) claims >50% fewer hangs and faster large-solution load, and C# Dev Kit 11.0 (pre-release, Oct 6 2026) claims 22–180x faster loads and ~81–85% less server memory on 155–407-project solutions. Rider still has open YouTrack reports of tens-of-GB memory on large solutions (late 2025). Solution filters/subsets remain the standard workaround.

### Cited Findings
- C# Dev Kit 11.0 (Drew Noakes, Oct 6 2026, pre-release, aligned with .NET 11, previous version 3.3). Orleans (155 projects): active file ready in up to 50.3s, now 0.53s; whole solution 50.3s to 2.3s. Aspire (407 projects): active file up to 84.1s to 0.47s; whole solution 84.1s to 3.0s. No-change build on Aspire 34.4s to 0.88s; single-file change 36.1s to 2.1s. Server memory: Orleans 1,307→208 MB, Roslyn (398 projects, 18,153 C# files) 2,000→379 MB, Aspire 2,072→316 MB (server process only; Roslyn LSP and VS Code memory excluded). Architecture: six managed processes folded into one Native AOT `CSDevKit` server. Project caches persist across loads, and *committed cache files can speed up fresh clones and git worktrees*. The active file loads first. The motivation cited explicitly includes agentic workflows running multiple instances in parallel. — [.NET Blog](https://devblogs.microsoft.com/dotnet/?p=60983); [Visual Studio Magazine, Oct 7 2026](https://visualstudiomagazine.com/articles/2026/10/07/microsoft-rebuilds-c-dev-kit-110-for-faster-loads-lower-memory-use.aspx)
- vscode-csharp #5733 (2023): a ~100-project solution saw memory jump after an update, with >4.5 GB used and no debugging running. Running two VS Code instances was hard. — [dotnet/vscode-csharp #5733](https://github.com/dotnet/vscode-csharp/issues/5733)
- VS 2026 GA announcement: faster startup, hangs cut by >50%, large .NET solution load-to-interactive faster than VS 2022, and UI-unresponsive time during large loads less than half of VS 2022's. — [VS Blog](https://devblogs.microsoft.com/visualstudio/visual-studio-2026-is-here-faster-smarter-and-a-hit-with-early-adopters/)
- An independent benchmark (Sept 2025) on a 48-project .NET Framework solution found VS 2026 build performance "hasn't increased by much." — [oksala.net](https://oksala.net/2025/09/17/visual-studio-2026-build-perfomance/)
- JetBrains (Oct 2 2025) cites a case going from 14s (VS 2022) to ~9s (VS 2026) with ReSharper. — [JetBrains blog](https://blog.jetbrains.com/dotnet/2025/10/02/resharper-and-visual-studio-2026/)
- VS options: "Allow parallel project initialization" speeds up reloading large .NET solutions, and turning off document/Solution Explorer state restore cuts load time. — [Microsoft Learn](https://learn.microsoft.com/en-us/visualstudio/ide/projects-and-solutions-options-dialog-box?view=visualstudio); [perf tips](https://learn.microsoft.com/en-us/visualstudio/ide/visual-studio-performance-tips-and-tricks?view=visualstudio)
- Historical: VS 15.6 (2018) parallel load made a 400+ project solution load 2–4x faster. 15.5 (2017) made C#/VB loads ~2x faster. Design-time builds are the main cost of loading. — [VS Blog 15.6](https://devblogs.microsoft.com/visualstudio/load-solutions-faster-with-visual-studio-2017-version-15-6/); [VS Blog 15.5](https://devblogs.microsoft.com/visualstudio/large-c-and-vb-solutions-load-significantly-faster-in-15-5-update)
- Solution filters (.slnf) are Microsoft's recommended way to load only some projects. — [VS 2022 launch session](https://learn.microsoft.com/en-us/shows/visual-studio-2022-launch-event/tips-and-tricks-load-solutions-faster-with-solution-filters)
- JetBrains (May 2020): the 162-project Rider solution uses ~2 GB in vanilla VS, much of it duplicate strings and syntax trees. — [JetBrains blog](https://blog.jetbrains.com/dotnet/2020/05/11/story-csproj-large-solutions-memory-usage/)
- Rider YouTrack: RIDER-132952 (2025.3, Nov 2025) hung at "loading projects" with Rider.Backend climbing up to 41 GB. RIDER-125163 (2025.1) UI hangs on big solutions. RIDER-127903 memory leak/CPU spikes in a large .NET solution. RIDER-110891 laggy typing with Solution-Wide Analysis. Workarounds users tried include disabling VCS plugins and Copilot. — [RIDER-132952](https://youtrack.jetbrains.com/projects/RIDER/issues/RIDER-132952/Runaway-memory-usage-since-upgrading-to-2025.3.0.2); [RIDER-125163](https://youtrack.jetbrains.com/projects/RIDER/issues/RIDER-125163/2025.1-UI-hangs-and-is-unresponsive-for-big-solutions); [RIDER-127903](https://youtrack.jetbrains.com/projects/RIDER/issues/RIDER-127903/Severe-Memory-Leak-CPU-Spikes-Freezes-in-Large-.NET-Solution); [RIDER-110891](https://youtrack.jetbrains.com/projects/RIDER/issues/RIDER-110891/Laggy-Slow-Typing-while-Solution-Wide-Analysis-is-enabled)
- Rider 2022.3 enabled server GC on the backend, and loading many-project solutions got "noticeably faster." — [JetBrains blog](https://blog.jetbrains.com/dotnet/2022/12/01/improved-startup-performance-in-rider-2022-3/)

### Inferences
- With Dev Kit 11 and VS 2026, Microsoft is closing the gap on IDE load and memory. A new tool should *not* compete as an IDE. It should feed IDEs: generate the right .slnx/.slnf subsets per developer or task, and possibly manage committed project caches (Dev Kit 11 now supports these).
- Agentic/parallel-worktree workflows are now a stated design driver at Microsoft. Worktree-aware caching and fast cold starts are timely features for a monorepo tool (Nx 22.7 also shipped worktree-aware caching, see section 4).
- Dev Kit 11 numbers are vendor-reported pre-release benchmarks. Expect real-world results to vary.

### Gaps
- No 2026 JetBrains blog on Rider large-solution performance was found.
- No published hard limits (max projects) for any IDE were found.
- The Dev Kit 11 stable release date is unknown (pre-release as of Oct 2026).

## 4. Commercial / SaaS offerings touching this space

### Takeaway
Nx is the only mainstream monorepo platform with first-party .NET support (`@nx/dotnet`, first beta Oct 2025, graduating from experimental in Nx 23 betas), and Nx Cloud is the monetization layer. Bazel RBE vendors (EngFlow, BuildBuddy) don't market .NET/MSBuild. Microsoft's MSBuildCache is free but preview-grade and Azure-oriented. Per-test impact analysis for .NET is mostly paid: Datadog Test Optimization, Tricentis SeaLights, and NCrunch (IDE-only). A new OSS entrant (dotnet-tia) is trying to commoditize it.

### Cited Findings
- `@nx/dotnet`: official plugin, installed via `nx add @nx/dotnet`. It uses MSBuild to analyze solution/project structure, and ProjectReferences become graph edges. It provides caching, affected-only builds, and distributed task execution. — [Nx docs](https://nx.dev/docs/technologies/dotnet/introduction); [Nx blog: OpenAPI type safety with @nx/dotnet](https://nx.dev/blog/dotnet-openapi-type-safety)
- Timeline: `@nx/dotnet` first appeared as 22.0.0-beta.8 (2025-10-19, "initial draft of the .NET plugin"). The Nx 22 launch highlighted .NET. The npm readme flagged it experimental (version 23.2.1 listed on jsDelivr). Nx 23.0.0-beta.24 changelog: "graduate @nx/dotnet — drop experimental banner and the /plugin export (#35895)." — [newreleases 22.0.0-beta.8](https://newreleases.io/project/npm/nx/release/22.0.0-beta.8); [jsDelivr @nx/dotnet](https://www.jsdelivr.com/package/npm/@nx/dotnet); [newreleases 23.0.0-beta.24](https://newreleases.io/project/github/nrwl/nx/release/23.0.0-beta.24)
- Conflict note: jsDelivr lists 23.2.1 while the graduation note is in a 23.0 beta changelog. Version labeling is inconsistent across sources, so check the current state on npm. — (same sources)
- Nx 22.7: task sandboxing ("catch hidden cache dependencies via real I/O tracing"), 7x less daemon memory, worktree-aware caching. Patch 22.7.7 included .NET sandbox fixes, e.g., declaring Directory.Build.props as inputs for analyzer dotnet tasks. This shows the MSBuild implicit-input problem in practice. — [Nx 22.7 blog](https://nx.dev/blog/nx-22-7-release); [newreleases 22.7.7](https://newreleases.io/project/npm/nx/release/22.7.7)
- The community plugin `@nx-dotnet/core` is deprecated: "no longer maintained. Please migrate to the official @nx/dotnet plugin (available in Nx 22+)." — [nx-dotnet GitHub](https://github.com/nx-dotnet/nx-dotnet)
- EngFlow supports Remote Execution API clients (Bazel, BuildStream, Goma, Pants, Please, Recc, Soong) plus CMake (beta). MSBuild/.NET is not listed. BuildBuddy positions itself as RBE for Bazel, with no .NET mentions found. — [EngFlow docs](https://docs.engflow.com/overview.html); [BuildBuddy RBE docs](https://docs.buildbuddy.io/docs/remote-build-execution); [Bazel RBE services list](https://bazel.google.cn/community/remote-execution-services?hl=en)
- Datadog Test Optimization / Test Impact Analysis supports .NET via `dd-trace ci run -- dotnet test` (TIA needs dd-trace ≥2.22.0 per some docs pages), with xUnit/NUnit/MSTest support and an unskippable trait for tests coverage can't capture. — [Datadog TIA .NET setup](https://docs.datadoghq.com/tests/test_impact_analysis/setup/dotnet/); [Datadog Test Optimization](https://docs.datadoghq.com/tests)
- dotnet-tia (OSS, MIT, v0.8.0 updated 2026-10-27, ~1.3K total downloads). It says per-test impact analysis for .NET has existed "only behind a paywall or SaaS (Datadog Test Optimization, Tricentis SeaLights, NCrunch – IDE-only)," and that free tools (dotnet-affected, Incrementalist) work only at project granularity. It is fully static (Roslyn MSBuildWorkspace, reverse reference graph BFS to test methods). Self-reported: NodaTime mean selection 35.5% over 20 replayed commits; FluentValidation 51.0%. Limits: reflection/serializers widen scope, C#-only depth. — [NuGet dotnet-tia](https://www.nuget.org/packages/dotnet-tia)
- microsoft/MSBuildCache is free and experimental, with Azure Blob / Azure Pipelines backends (see section 2). — [microsoft/MSBuildCache](https://github.com/microsoft/MSBuildCache)

### Inferences
- Nx is the main incumbent for a new .NET monorepo tool. Its weaknesses for .NET shops: it needs a Node/npm toolchain, its inputs/outputs are modeled on JS tasks (the sandboxing patches show MSBuild's implicit inputs are hard to model), and phan.nz-style worries about wrapping the dotnet CLI apply.
- Bazel RBE vendors ignoring .NET leaves remote execution/caching for MSBuild without a commercial champion, apart from Nx Cloud at the task level and Microsoft's preview plugin.
- Test impact analysis is a proven paid category in .NET. Datadog, SeaLights, and NCrunch all charge, which supports willingness to pay.

### Gaps
- Not researched or verified: Gradle Develocity .NET support (none found), JetBrains TeamCity monorepo features (build chains, VCS triggers by path, build cache), Depot (Docker/GitHub Actions runners, no .NET-specific product known), NCrunch pricing/features beyond the third-party mention, and Nx Cloud pricing.
- No startups specifically targeting .NET build acceleration were identified in this pass.

## 5. Gaps & opportunities for a new .NET monorepo tool

### Takeaway
The clearest openings are: (1) a vendor-neutral, production-grade remote cache for MSBuild that works with any CI and any storage; (2) affected detection that fully understands MSBuild semantics (Directory.Build.props/targets, CPM files, imports, globs, analyzers, source generators, non-code inputs) down to the test level; (3) .slnx/.slnf generation and sync for subsets and worktrees; (4) CPM governance and policy; (5) release automation for many NuGet packages; (6) dependency-boundary enforcement. Microsoft is working on enabling primitives (persistent evaluation cache epic, MSBuild project-cache plugin API, Dev Kit project caches), which lowers the cost of building on top. The demand evidence is real but diffuse: lots of small tools, low-vote issues, and paid TIA products.

### Cited Findings
- MSBuild epic #14234 "Speed up evaluations across build with a persistent evaluation cache" (opened Jul 1 2026, open, Area: Evaluation/Performance): caches last only one build, and "We have _billions_ of invocations each month." The proposal includes sharing persistent cache files across hosts via remote stores or CI artifact storage. About 6 of 9 sub-issues done (progress display garbled). Sub-issues include an evaluation input observation layer for cache invalidation (#14688), evaluation metrics via System.Diagnostics.Metrics (#14419), and dotnet/sdk#56045 (CLI commands that change "immutable" state should kill/invalidate build servers). — [dotnet/msbuild #14234](https://github.com/dotnet/msbuild/issues/14234); [dotnet/sdk #56045](https://github.com/dotnet/sdk/issues/56045)
- MSBuild already has a third-party project-cache plugin API (`ProjectCachePluginBase`), documented late (PR #9329). — [ProjectCachePluginBase source](https://source.dot.net/Microsoft.Build/BackEnd/Components/ProjectCache/ProjectCachePluginBase.cs.html); [discussion #8718](https://github.com/dotnet/msbuild/discussions/8718)
- Requests to expose MSBuild's up-to-date hash (#9122, closed) and to make evaluation machine-agnostic for cloud caching (#6182, backlog) show the hooks third parties need are not all there. — [#9122](https://github.com/dotnet/msbuild/issues/9122); [#6182](https://github.com/dotnet/msbuild/issues/6182)
- Nx needed .NET-specific fixes to declare Directory.Build.props as task inputs (22.7.7). Correct input modeling for MSBuild is hard. — [newreleases 22.7.7](https://newreleases.io/project/npm/nx/release/22.7.7)
- Project-level affected tools (dotnet-affected, Incrementalist) vs. per-test TIA (paywalled), per dotnet-tia's README. — [NuGet dotnet-tia](https://www.nuget.org/packages/dotnet-tia)
- rules_dotnet users ask for Gazelle-style BUILD generation (#258). Hand-written build metadata is a non-starter. — [rules_dotnet #258](https://github.com/bazelbuild/rules_dotnet/issues/258)
- Dev Kit 11 committed project caches for clones/worktrees, and agentic parallel workflows as a design driver. — [.NET Blog](https://devblogs.microsoft.com/dotnet/?p=60983)
- The VMR team needed custom tooling for conflicts, code-flow PRs, branch snapping, and metadata. Hybrid mono/poly setups lack off-the-shelf tooling. — [.NET Blog](https://devblogs.microsoft.com/dotnet/how-we-synchronize-dotnets-virtual-monorepo/)
- Lockstep framework upgrades are a monorepo complaint ("everything stays old"). — [HN 44113973](https://news.ycombinator.com/item?id=44113973)
- CPM edge cases (NU1109 pin floors, VersionOverride, transitive pinning, source-build exceptions) in real repos. — [dotnet/fsharp #20084](https://github.com/dotnet/fsharp/pull/20084); [NU1109](https://learn.microsoft.com/en-gb/nuget/reference/errors-and-warnings/nu1109)
- Mixed-language monorepos (node_modules inside .NET solutions) slow .NET tooling. — [dotnet/format #1485](https://github.com/dotnet/format/pull/1485)

### Inferences
Opportunity map, ranked by evidence strength:
1. **MSBuild-native remote cache (strong).** It should be storage-agnostic (S3/GCS/Azure Blob/HTTP), CI-agnostic (GitHub Actions, GitLab, TeamCity, Azure Pipelines), and safe for untrusted PRs (read-only for forks). Build it on the ProjectCachePlugin API and, once it ships, the persistent evaluation cache. MSBuildCache shows it can be done but is preview, Azure-tied, and warns about trust, which is the gap.
2. **Semantics-aware affected detection plus test selection (strong).** Model the full MSBuild input closure: imports, Directory.Build.*, Directory.Packages.props, global.json, .editorconfig, analyzers/source generators, content globs, launchSettings, Dockerfiles. Then add Roslyn-level test selection. Existing OSS covers projects only, and per-test TIA is paywalled. Combining them in one tool is differentiated.
3. **Solution/IDE-view generation (medium).** Now that .slnx is the default (.NET 10), generate per-team/per-task .slnx/.slnf from the graph ("open everything affected by X"), keep them in sync in CI, and integrate with worktrees/agents.
4. **CPM governance (medium).** Lint and policy for VersionOverride usage, pinning floors, duplicate/drifting versions, vulnerability bumps, and staged framework upgrades (letting parts of the repo move to a new TFM/SDK at different paces, which targets the "everything stays old" complaint).
5. **Many-package NuGet release automation (medium, little direct evidence collected).** Versioning, changelogs, and publishing for dozens to hundreds of packages, the way Azure SDK EngSys does with its in-house system.
6. **Boundary enforcement (weak evidence in this pass).** Project-reference rules or tags, similar to Nx module boundaries, but native to MSBuild.
7. **Hybrid mono/poly sync (niche, high complexity).** VMR-style code flow as a product. Only very large orgs need it.
- Positioning: be .NET-native (a dotnet tool or MSBuild SDK, no Node requirement), preserve IDE/debugger fidelity (answering phan.nz's Rider debugging worry), and feed IDEs rather than replace them.

### Gaps
- Upvote counts on the key GitHub issues (#14234 showed "Reactions unavailable"; #9122/#6182 counts not captured) were not obtained, so demand cannot be quantified from issue reactions.
- No direct evidence was gathered on NuGet multi-package release automation pain (e.g., threads on Nerdbank.GitVersioning/MinVer/GitVersion in monorepos) or on boundary enforcement demand.
- No market-size data (number of .NET shops with more than 100-project repos) was found.
- Reddit sentiment and "what I wish existed" threads could not be retrieved (blocked domain). This is the biggest gap for the "what developers say they wish existed" question.
