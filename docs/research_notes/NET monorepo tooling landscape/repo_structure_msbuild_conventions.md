# Repository structure conventions and MSBuild-level shared configuration for .NET monorepos (state as of Oct 2026)

Scope note: research done 2026-10-28 with a budget of about 15 tool calls. Claims are tagged with source dates where they matter. Items I could not verify within budget are listed under "Gaps" and are not presented as fact.

## 1. Directory.Build.props / .targets / Directory.Solution.props: hierarchy, import semantics, chaining, pitfalls, typical contents

### Takeaway
MSBuild imports only the *first* `Directory.Build.props` (and separately the first `Directory.Build.targets`) found by walking up from the **project** directory, not the solution directory. Multi-level setups only work if every inner file explicitly chains to the outer one with `GetPathOfFileAbove`. A monorepo tool should generate and check that chain. `.props` holds early defaults (SDK-input properties, output paths). `.targets` holds late overrides and custom targets that need values set by the project or by packages.

### Cited Findings
- The special files MSBuild recognizes: `Directory.Build.props` (imported early by Microsoft.Common.props), `Directory.Build.targets` (imported late by Microsoft.Common.targets), `Directory.Build.rsp` (default command-line args, **CLI builds only**: msbuild.exe / `dotnet build`, not the VS IDE), and `Directory.Solution.props` / `Directory.Solution.targets` (apply only to builds of a solution file). Doc updated 2026-10-20. — [MS Learn: Customize the build by folder](https://learn.microsoft.com/en-us/visualstudio/msbuild/customize-by-directory)
- Search algorithm: MSBuild walks upward from `$(MSBuildProjectFullPath)` and stops at the first `Directory.Build.props` it finds. "The location of the solution file is irrelevant to Directory.Build.props." — [MS Learn](https://learn.microsoft.com/en-us/visualstudio/msbuild/customize-by-directory)
- Canonical multi-level merge snippet, placed in each inner file (e.g. `src/Directory.Build.props`, `test/Directory.Build.props`): `<Import Project="$([MSBuild]::GetPathOfFileAbove('Directory.Build.props', '$(MSBuildThisFileDirectory)../'))" Condition="'' != $([MSBuild]::GetPathOfFileAbove('Directory.Build.props', '$(MSBuildThisFileDirectory)../'))" />`. Rule of thumb: "the first Directory.Build.props that doesn't import anything is where MSBuild stops." — [MS Learn](https://learn.microsoft.com/en-us/visualstudio/msbuild/customize-by-directory)
- Escape hatches: `$(DirectoryBuildPropsPath)`, `$(ImportDirectoryBuildProps)`, `$(DirectoryBuildTargetsPath)`, `$(ImportDirectoryBuildTargets)`. The two Import flags default to true and can be set to false to opt a project out. — [MS Learn](https://learn.microsoft.com/en-us/visualstudio/msbuild/customize-by-directory)
- Import order semantics: properties in `.props` are defaults that the project can override. `.props` cannot see properties defined later (they evaluate to empty). `Directory.Build.targets` is imported *after* NuGet package `.targets`, so it can override package and SDK logic "regardless of what the individual projects set." — [MS Learn](https://learn.microsoft.com/en-us/visualstudio/msbuild/customize-by-directory)
- Some properties must go in `.props` because the SDK reads them early. Example: `BaseIntermediateOutputPath` is consumed in Microsoft.Common.props (to derive `MSBuildProjectExtensionsPath`). Setting it in the csproj body has no effect. Debug with `msbuild /pp:out.xml`. — [MS Learn](https://learn.microsoft.com/en-us/visualstudio/msbuild/customize-by-directory)
- Pitfall: `OutDir` is absolute and bypasses config/TFM/RID subfolders. The docs recommend `BaseOutputPath` instead. — [MS Learn](https://learn.microsoft.com/en-us/visualstudio/msbuild/customize-by-directory)
- Pitfall: on Linux the file name is case-sensitive (`Directory.Build.props` exactly). — [MS Learn](https://learn.microsoft.com/en-us/visualstudio/msbuild/customize-by-directory)
- Pitfall: imported files are read at evaluation time, so build steps that modify them (e.g. PreBuildEvent) take effect only on the next invocation. VS caches project info, so after editing these files you must reload the solution or project. Imports are re-evaluated for every project build, including each multitarget inner build. — [MS Learn](https://learn.microsoft.com/en-us/visualstudio/msbuild/customize-by-directory)
- The docs' own examples of typical `.targets` contents are `EnforceCodeStyleInBuild`, `TreatWarningsAsErrors`, and custom validation targets hooked `BeforeTargets="CoreCompile"`. — [MS Learn](https://learn.microsoft.com/en-us/visualstudio/msbuild/customize-by-directory)
- Directory.Solution.props/.targets can import the Traversal SDK so that solution builds gain Traversal behaviors (e.g. `TraversalSkipUnsupportedProjects`). — [MSBuildSdks Traversal README](https://github.com/microsoft/MSBuildSdks/blob/main/src/Traversal/README.md)
- The Arcade-SDK repo pattern (dotnet/* repos): root `Directory.Build.props` imports `Sdk.props` from `Microsoft.DotNet.Arcade.Sdk` first, then sets shared properties (`PackageLicenseExpression`, `StrongNameKeyId`). Root `Directory.Build.targets` imports Arcade's `Sdk.targets`. — [dotnet/arcade ArcadeSdk.md](https://github.com/dotnet/arcade/blob/main/Documentation/ArcadeSdk.md)

### Inferences
- A generator/linter should (a) emit `GetPathOfFileAbove` chaining in every nested `Directory.Build.props` and `.targets`, (b) flag nested files that do *not* chain, since this is the most common silent-misconfiguration bug, (c) flag wrong-case filenames, and (d) flag early-read properties (`BaseIntermediateOutputPath`, `ArtifactsPath`, `UseArtifactsOutput`, `MSBuildProjectExtensionsPath`) set outside `.props`.
- Typical root `.props` contents: `LangVersion`, `Nullable`, `ImplicitUsings`, `TreatWarningsAsErrors`, `AnalysisLevel`, artifacts output, company/copyright/license metadata, `IsPackable=false` by default, plus a `src`/`test` split such as `IsTestProject`. Most of these are common practice rather than drawn from a single source here. The docs confirm only the analyzer and warnings items above.
- `Directory.Build.rsp` is attractive for CI defaults (`-bl`, `-m`, `-nr:false`), but it does not apply inside VS, so it must not carry semantics the IDE build needs.

### Gaps
- I did not verify MSBuild changes in .NET 10/11 to Directory.Build.* semantics (none surfaced). Nor did I confirm Rider's or C# Dev Kit's caching behavior when these files change.

## 2. Central Package Management (Directory.Packages.props)

### Takeaway
CPM is mature and is the default recommendation: `ManagePackageVersionsCentrally`, `PackageVersion`, `VersionOverride`, `GlobalPackageReference`, `CentralPackageTransitivePinningEnabled`. In monorepos the sharp edges are: only the nearest `Directory.Packages.props` is imported, so parents must be chained manually; NU1507 fires when there are multiple feeds without package source mapping; transitive pinning leaks into packed nuspecs; and `VersionOverride` erodes centralization unless it is disabled.

### Cited Findings
- Enable with `ManagePackageVersionsCentrally=true` in a root `Directory.Packages.props` (scaffold with `dotnet new packagesprops`). Projects then use `<PackageReference Include="X" />` without `Version`. Applies to all PackageReference projects, including legacy csproj. Doc updated 2026-03-03. — [MS Learn: CPM](https://learn.microsoft.com/en-us/nuget/consume-packages/central-package-management)
- Hierarchy: "Only one Directory.Packages.props file is evaluated for a given project by default". The nearest one wins. To inherit a parent, import it explicitly with `GetPathOfFileAbove(Directory.Packages.props, $(MSBuildThisFileDirectory)..)`, then use `<PackageVersion Update=... />` to override. — [MS Learn: CPM](https://learn.microsoft.com/en-us/nuget/consume-packages/central-package-management)
- Per-TFM versions: use conditional `PackageVersion` items (`Condition="'$(TargetFramework)' == 'netstandard2.0'"`). — [MS Learn: CPM](https://learn.microsoft.com/en-us/nuget/consume-packages/central-package-management)
- Transitive pinning (`CentralPackageTransitivePinningEnabled=true`) promotes a transitive dependency to top-level when a central version exists. Pinning to a lower version (a downgrade) is an NU1109 error. When packing, pinned transitives become explicit nuspec dependencies, so evaluate it carefully for libraries. — [MS Learn: CPM](https://learn.microsoft.com/en-us/nuget/consume-packages/central-package-management)
- `VersionOverride` on a PackageReference wins over the central version. Setting `CentralPackageVersionOverrideEnabled=false` turns any `VersionOverride` into a restore error, which is a useful enforcement switch. — [MS Learn: CPM](https://learn.microsoft.com/en-us/nuget/consume-packages/central-package-management)
- Opt out per project with `ManagePackageVersionsCentrally=false`. — [MS Learn: CPM](https://learn.microsoft.com/en-us/nuget/consume-packages/central-package-management)
- `GlobalPackageReference` (requires VS 17.4+, SDK 7.0.100-preview7+, NuGet 6.4+) adds a package to every project with `IncludeAssets="Runtime;Build;Native;contentFiles;Analyzers"` and `PrivateAssets="All"`. It is intended for versioning, analyzer, and build-extension packages such as Nerdbank.GitVersioning. — [MS Learn: CPM](https://learn.microsoft.com/en-us/nuget/consume-packages/central-package-management)
- NU1507 warning: with CPM and more than one package source, NuGet warns unless package source mapping is configured or only one source is used. — [MS Learn: CPM](https://learn.microsoft.com/en-us/nuget/consume-packages/central-package-management)
- Example of GlobalPackageReference in practice: ReferenceTrimmer recommends installing itself repo-wide via `GlobalPackageReference` in `Directory.Packages.props`. — [ReferenceTrimmer README](https://github.com/dfederm/ReferenceTrimmer)
- Legacy: the MSBuildSdks repo README currently lists only Traversal, NoTargets, Artifacts, CopyOnWrite, and RunVSTest. `Microsoft.Build.CentralPackageVersions` is no longer among the listed SDKs. — [microsoft/MSBuildSdks](https://github.com/microsoft/MSBuildSdks)
- Arcade repos pre-date CPM and centralize versions in `eng/Versions.props` (version prefix, prerelease label, dependency versions, `UsingTool{Name}` opt-ins). — [dotnet/arcade ArcadeSdk.md](https://github.com/dotnet/arcade/blob/main/Documentation/ArcadeSdk.md)

### Inferences
- A monorepo tool should support (1) a single root CPM file as the default, (2) optional nested CPM files with enforced parent-import chaining, mirroring Directory.Build.props, and (3) policy toggles for `CentralPackageVersionOverrideEnabled=false`, transitive pinning on for apps and cautious for packable libraries, and package source mapping in NuGet.config so NU1507 is resolved rather than suppressed.
- Because `Directory.Packages.props` is just MSBuild, nested files can silently shadow root versions. A lint rule like "every nested CPM file must import its parent" is high-value.

### Gaps
- I did not verify CentralisedPackageConverter (Webreaper) status or flags, other migration tools, or adoption statistics. No source was fetched.
- I did not verify .NET 10 / NuGet 7.x CPM additions (e.g. any new warnings, `CentralPackageFloatingVersionsEnabled` status, or `dotnet package add` behavior under CPM). The CPM page fetched did not mention floating-version support.
- I did not cover the NuGet.config hierarchy (nearest-to-root merge, `<clear/>`) with a fetched source in this pass. Arcade's pattern is a root NuGet.config that clears sources and adds `dotnet-eng` and dependency feeds. — [ArcadeSdk.md](https://github.com/dotnet/arcade/blob/main/Documentation/ArcadeSdk.md)

## 3. Solution files: .slnx, .slnf, SlnGen, "no solution", IDE scale

### Takeaway
As of the .NET 10 SDK (Nov 2025), `dotnet new sln` emits **.slnx by default**, and `dotnet sln <x>.sln migrate` converts existing files. .slnx support has existed since SDK 9.0.200 (early 2025). VS 2022 17.13+, VS 2026, Rider 2024.3+, and C# Dev Kit all read it. Solution filters (.slnf) still work on top of .slnx. SlnGen, the "generate solutions on demand from the MSBuild graph" tool, is actively maintained (releases Oct 2026). Its .slnx output is being added (new parser plus .slnx tests in 12.0.47, Sep 2026), but I could not confirm it is the default. For very large repos, traversal projects (dirs.proj) can replace solutions for builds, with solutions or filters generated for IDE use.

### Cited Findings
- Breaking change in .NET 10 SDK: `dotnet new sln` creates an SLNX file by default. Use `--format sln` for the legacy format. .NET 9 and earlier create .sln. — [MS Learn breaking change: dotnet new sln slnx default](https://learn.microsoft.com/en-us/dotnet/core/compatibility/sdk/10.0/dotnet-new-sln-slnx-default)
- SDK 9.0.200 is the first version with full .slnx support. Migration is `dotnet sln MySolution.sln migrate`, which writes a new .slnx and leaves the .sln in place. Do not just rename .sln to .slnx. — [Milan Jovanovic migration guide](https://milanjovanovic.tech/blog/the-new-slnx-solution-format-migration-guide); [codewithmukesh](https://codewithmukesh.com/blog/slnx-solution-format-dotnet/)
- .NET CLI slnx support was announced in March 2025 (SDK 9.0.200, initially described as experimental), allowing `dotnet build`/`dotnet test` against .slnx directly. — [InfoQ, Apr 2025](https://www.infoq.com/news/2025/04/dotnet-cli-slxn-support)
- The third-party `Slnx` NuGet package is deprecated "because MSBuild now officially has support for reading/writing/building SLNX files". — [NuGet: Slnx](https://www-1.nuget.org/packages/Slnx)
- VS blog (Mar 13 2025): solution filters (.slnf) "will continue to work with the new .SLNX format", and C# Dev Kit for VS Code fully supports .slnx. — [VS blog](https://devblogs.microsoft.com/visualstudio/?p=252703)
- Rider has supported .slnx since 2024.3. There were reported rough edges, e.g. publish from Rider failing with .slnx while the CLI worked (tracking issue RIDER-110777). — [JetBrains blog Oct 2024](https://blog.jetbrains.com/dotnet/2024/10/04/support-for-slnx-solution-files/); [Rider support thread](https://rider-support.jetbrains.com/hc/en-us/community/posts/23939311081106/comments/23939370541714)
- VS 2026 default format: evidence is indirect. A Microsoft Q&A thread describes a "Default Solution File Format" setting in VS 2026, with VS continuing to generate .slnx even after the user set .sln. — [MS Q&A](https://learn.microsoft.com/en-us/answers/questions/5775749/how-do-i-change-the-solution-file-for-net-10). A Radzen forum report notes a Razor-file error with .slnx in VS 2026. — [Radzen forum](https://forum.radzen.com/t/error-when-opening-a-razor-file-that-is-part-of-a-slnx-solution/21157)
- SlnGen releases (microsoft/slngen): 12.0.58 and 12.0.57 (23 Oct [2026]) fixed duplicate project names across solution folders. 12.0.47 (23 Sep [2026]) added "the new solution parser" to "later enable .slnx support", tests for ".slnx solution file generation and --format option", and net11 support. 12.0.29 (13 Nov [2025]) moved to .NET 10 GA. The releases page shows month and day only; I inferred the years from the .NET 10 GA ordering. — [slngen releases](https://github.com/microsoft/slngen/releases)
- Solution filters: VS "filtered solutions" load only a subset of projects. Rider also supports .slnf (help pages exist through Rider 2026.2). — [MS Learn: Filtered solutions](https://learn.microsoft.com/visualstudio/ide/filtered-solutions); [Rider: Solution filters](https://www.jetbrains.com/help/rider/Solution_filters.html)
- There is a NuGet tool, `SolutionFilterGenerator`, for generating .slnf files. — [NuGet: SolutionFilterGenerator](https://www-0.nuget.org/packages/SolutionFilterGenerator)
- C# Dev Kit: in Feb 2025 a "solution-less workspace" mode was in preview, letting you work on C# projects without a .sln. — [InfoQ Feb 2025](https://www.infoq.com/news/2025/02/csharp-dev-kit-update). A secondary source reports that C# Dev Kit 11.0 added project caching, a compact project representation, fewer processes, and a Native AOT server, giving faster startup and lower memory on large solutions. — [C# Corner](https://www.c-sharpcorner.com/article/c-sharp-dev-kit-gets-faster-and-uses-less-memory). VS Code project-management doc: [code.visualstudio.com](https://code.visualstudio.com/docs/csharp/project-management)
- Visual Studio "Open Folder" lets you work without projects or solutions. — [MS Learn: Develop code without projects or solutions](https://learn.microsoft.com/en-us/visualstudio/ide/develop-code-in-visual-studio-without-projects-or-solutions)
- "No solution" build approach: Microsoft.Build.Traversal `dirs.proj` files list projects via `ProjectReference` (globs like `src\**\*.*proj` are supported). They can nest (`src\dirs.proj`, `test\dirs.proj`) so you can build from any folder, which "often replac[es] Visual Studio solution files for large trees". Per-reference `Test="false"`, plus `TestInParallel` and `TestContinueOnError`. SDK version can be pinned in global.json `msbuild-sdks`. — [Traversal README](https://github.com/microsoft/MSBuildSdks/blob/main/src/Traversal/README.md); [MSBuildSdks](https://github.com/microsoft/MSBuildSdks)
- Arcade repos use optional `eng/Build.props` with `ProjectToBuild` items to choose what to build, rather than relying on one master solution. — [ArcadeSdk.md](https://github.com/dotnet/arcade/blob/main/Documentation/ArcadeSdk.md)

### Inferences
- Recommended conventions for a new tool: generate .slnx (not .sln) by default. Treat solutions as *derived views*: a root .slnx or traversal for CI, plus per-domain .slnx/.slnf generated from the project graph (SlnGen-style), rather than hand-maintained source of truth. Detect projects missing from every solution or traversal ("orphans").
- Because .slnf files are bound to one specific solution path, regenerating or renaming the parent solution breaks filters. A generator should regenerate filters together with their parent.
- SlnGen's .slnx output appears to be in progress. A new tool could differentiate by offering first-class .slnx/.slnf generation from globs or project graph closure ("open project X plus all its dependencies").

### Gaps
- I found no authoritative benchmarks of VS 2026 / Rider / C# Dev Kit load times for 500+ or 1000+ project solutions, and did not verify the VS "lightweight solution load" status (deprecated in VS 2019, from memory, unverified here).
- I could not confirm from an official doc whether VS 2026 defaults new solutions to .slnx, or whether `dotnet sln migrate` changed in .NET 10. Nor whether Rider issue RIDER-110777 is resolved.
- Whether C# Dev Kit's solution-less mode reached GA is unconfirmed.

## 4. SDK features relevant to monorepos

### Takeaway
Pin the SDK with global.json. Since .NET 10, global.json also selects the test runner (`"test": {"runner": "Microsoft.Testing.Platform"}`) and can point to repo-local SDKs (`sdk.paths`, `errorMessage`). Use the artifacts output layout (.NET 8+) to centralize bin/obj. Pin MSBuild SDKs (Traversal, NoTargets, Arcade) in global.json `msbuild-sdks`. .NET 10 file-based apps (`dotnet run app.cs`) are relevant for repo scripts and tooling, but they inherit Directory.Build.props and NuGet config from parent folders, so isolate them.

### Cited Findings
- **global.json test runner (.NET 10):** MTP mode for `dotnet test` is selected in global.json with a `"test"` section `"runner": "Microsoft.Testing.Platform"`. This replaced the now-obsolete MSBuild property `TestingPlatformDotnetTestSupport`. Running an MTP v2 project under the .NET 10 SDK without it errors. Requires MTP 1.7+. Once enabled, *all* test projects must use MTP: VSTest projects become an error. Pre-RC2 SDKs used a `dotnet.config` file instead. — [xUnit v3 3.2.0-pre.10 release notes](https://xunit.net/releases/v3/3.2.0-pre.10); [MS Learn: dotnet test (MTP)](https://learn.microsoft.com/da-dk/dotnet/core/tools/dotnet-test-mtp)
- Third-party (Oct 2026): global.json is found by searching upward from the current working directory, and property names are case-sensitive. A CI pipeline running from a different directory silently fell back to VSTest. The same source says a `DOTNET_TEST_RUNNER` env var override arrives in .NET 11 SDK Preview 6. — [startdebugging.net, Oct 2026](https://startdebugging.net/de/2026/10/fix-dotnet-test-falls-back-to-vstest-in-linux-ci-microsoft-testing-platform/) (secondary, unverified against an official doc)
- **global.json `sdk.paths` / `errorMessage` (.NET 10):** the resolver checks listed folders in order (e.g. `.dotnet` then `$host$`) and uses the first SDK satisfying the version constraint. Relative paths are resolved relative to the global.json file. It requires a .NET 10+ host, and older hosts silently ignore it. `errorMessage` is shown when no compatible SDK is found. Introduced in .NET 10 Preview 3. — [MS Learn: global.json](https://learn.microsoft.com/en-us/dotnet/core/tools/global-json); [MS Learn: test prerelease SDK locally](https://learn.microsoft.com/en-us/dotnet/core/tools/test-prerelease-sdk-locally); [dotnet/docs PR 52893](https://github.com/dotnet/docs/pull/52893.diff)
- global.json `msbuild-sdks` pins MSBuild project SDK versions repo-wide, e.g. so `<Project Sdk="Microsoft.Build.Traversal">` needs no inline version. — [MSBuildSdks](https://github.com/microsoft/MSBuildSdks). Arcade pins both `tools.dotnet` and `msbuild-sdks.Microsoft.DotNet.Arcade.Sdk` there. — [ArcadeSdk.md](https://github.com/dotnet/arcade/blob/main/Documentation/ArcadeSdk.md)
- **Artifacts output (.NET 8+):** `UseArtifactsOutput=true` or `ArtifactsPath` (in Directory.Build.props). The default root is `artifacts/` next to Directory.Build.props, laid out as `artifacts/<bin|obj|publish|package>/<ProjectName>/<pivot>`, where the pivot is a lowercased combination of config, TFM, and RID, e.g. `debug_net8.0`, `release_linux-x64`. Packages go to `artifacts/package/<config>` with no project folder. Customize with `ArtifactsProjectName` and `ArtifactsPivots`. Scaffold with `dotnet new buildprops --use-artifacts`. Doc updated 2026-09-25. — [MS Learn: Artifacts output layout](https://learn.microsoft.com/en-us/dotnet/core/sdk/artifacts-output)
- Arcade's equivalent single-root layout (pre-dates UseArtifactsOutput): `artifacts/bin/$(MSBuildProjectName)/$(Configuration)`, `artifacts/obj/...`, `artifacts/packages/$(Configuration)/{Shipping,NonShipping,...}`, `artifacts/TestResults`, `artifacts/log/$(Configuration)/Build.binlog`, `artifacts/tmp`, `artifacts/toolset`. — [ArcadeSdk.md](https://github.com/dotnet/arcade/blob/main/Documentation/ArcadeSdk.md)
- **MSBuild SDKs (microsoft/MSBuildSdks):** Microsoft.Build.Traversal (dirs.proj aggregation), Microsoft.Build.NoTargets (utility projects that do not compile an assembly), Microsoft.Build.Artifacts (stage outputs), Microsoft.Build.CopyOnWrite (CoW copies on Windows Dev Drive/ReFS, a monorepo build-speed win), Microsoft.Build.RunVSTest (run VSTest in the Test target concurrently with the build). — [microsoft/MSBuildSdks](https://github.com/microsoft/MSBuildSdks)
- **Directory.Build.rsp:** default command-line arguments for CLI builds only. — [MS Learn](https://learn.microsoft.com/en-us/visualstudio/msbuild/customize-by-directory)
- **File-based apps (.NET 10):** `dotnet run app.cs` (or `dotnet app.cs`) builds via an implicit temp project. `#:package` (since .NET 10 preview 4), `#:project ../X/X.csproj`, and `#:property` directives are supported. They respect Directory.Build.props and NuGet config in the same or parent folders. Microsoft advises against placing file-based apps inside a `.csproj` directory tree because the implicit build files can interfere, and recommends a separate scripts folder with its own Directory.Build.props. Native AOT publish is on by default (`#:property PublishAot=false` to disable). — [MS Learn: File-based apps](https://learn.microsoft.com/en-us/dotnet/core/sdk/file-based-apps); [NDepend blog](https://blog.ndepend.com/net-10-0-dotnet-run-app-cs-or-file-based-program). .NET 11 adds further changes (e.g. `#:include` multi-file), per secondary sources. — [startdebugging.net Aug 2026](https://startdebugging.net/2026/08/how-to-run-a-file-based-csharp-app-with-dotnet-run-in-dotnet-11/); [antondevtips](https://antondevtips.com/blog/building-file-based-apps-in-dotnet-with-multi-file-support)
- Aspire can orchestrate C# file-based apps. — [aspire.dev: C# file-based apps](https://aspire.dev/integrations/dotnet/csharp-file-based-apps)

### Inferences
- A monorepo tool should generate a root `global.json` with `sdk.version` + `rollForward` (e.g. `latestFeature`/`latestPatch`), the `test.runner` choice (and an enforcement check that all test projects match it), and `msbuild-sdks` pins. Optionally add `sdk.paths: [".dotnet", "$host$"]` with an `errorMessage` pointing to the bootstrap script, mirroring Arcade's repo-local SDK approach.
- Because global.json is discovered relative to the CWD, CI scripts should `cd` to the repo root or the tool should warn when nested global.json files exist.
- A good place for repo automation is file-based apps under `eng/scripts/` (or `tools/`) with an isolating `Directory.Build.props` (and probably a `Directory.Packages.props` opting out of CPM if needed). The tool could scaffold that.
- Centralized `.editorconfig` at the root (with `EnforceCodeStyleInBuild` and `AnalysisLevel` in Directory.Build.props) is standard practice. In this pass I sourced only the `EnforceCodeStyleInBuild`/`TreatWarningsAsErrors` example.

### Gaps
- I did not fetch the .editorconfig/global AnalyzerConfig (`.globalconfig`) precedence docs or the NuGet.config hierarchy docs.
- I did not verify the exact global.json `rollForward` values list or `allowPrerelease` behavior changes in .NET 10.
- I did not verify whether the CopyOnWrite or RunVSTest SDKs have current releases or caveats.

## 5. Folder layouts and enforcement tools

### Takeaway
The de facto layout is `src/`, `test(s)/`, `eng/` (engineering/build infra), `artifacts/` (gitignored output), and `samples/`/`docs/`, with root-level `global.json`, `NuGet.config`, `Directory.Build.props/.targets`, `Directory.Packages.props`, `.editorconfig`, and thin `build.cmd`/`build.sh` wrappers. The Arcade SDK documents this precisely. Enforcement is layered: MSBuild-level reference hygiene (ReferenceTrimmer), CPM override switches, analyzers, and architecture tests.

### Cited Findings
- Arcade-SDK repo layout (used across dotnet/* repos):
  - `eng/common/`: shared, identical-across-repos bootstrap scripts (`build.ps1/.sh`, `tools.ps1/.sh`, `CIBuild.cmd`/`cibuild.sh`, `internal/Tools.csproj`, `internal/NuGet.config`).
  - Optional `eng/Build.props` (`ProjectToBuild`), `eng/Versions.props`, optional `eng/Tools.props`, `eng/Signing.props`, `eng/Publishing.props`, `eng/AfterSolutionBuild.targets`, `eng/AfterSigning.targets`.
  - Root `global.json`, `NuGet.config`, `Directory.Build.props/.targets`, `license.txt`, and `build.cmd`/`build.sh` wrappers "without adding logic".
  - `src/` with any substructure.
  - Test projects suffixed `.UnitTests`, `.IntegrationTests`, `.PerformanceTests` (or `.Tests`).
  - `artifacts/` as the single output root.
  - [ArcadeSdk.md](https://github.com/dotnet/arcade/blob/main/Documentation/ArcadeSdk.md)
- ReferenceTrimmer (dfederm) finds removable references. RT0001 flags an unnecessary reference, RT0002 an unnecessary ProjectReference, RT0003 an unnecessary PackageReference, and RT0000 warns that doc generation is off. Install it repo-wide via `GlobalPackageReference`, or make it opt-in via `EnableReferenceTrimmer`. It needs `GenerateDocumentationFile=true` and works best with IDE0005. An experimental `ReferenceTrimmerUseSymbolAnalysis` mode avoids the doc requirement. Limitations: it skips SDK-implied and framework references, transitive project refs, and packages with build/ props/targets by default. — [ReferenceTrimmer](https://github.com/dfederm/ReferenceTrimmer)
- Microsoft.Build.Artifacts stages build outputs (an alternative to ad-hoc copy targets). — [MSBuildSdks](https://github.com/microsoft/MSBuildSdks)
- `CentralPackageVersionOverrideEnabled=false` is a built-in enforcement switch for version drift. — [MS Learn: CPM](https://learn.microsoft.com/en-us/nuget/consume-packages/central-package-management)

### Inferences
- A new tool could enforce project-reference direction rules (e.g. `src` must not reference `test`; domain A must not reference domain B internals) with a `Directory.Build.targets` target that inspects `@(ProjectReference)` `BeforeTargets="ResolveProjectReferences"`, an approach the docs' `BeforeTargets` validation example supports in principle. That would be a cheap MSBuild-native alternative to architecture-test libraries.
- Naming conventions (`*.Tests`, `*.UnitTests`, `*.IntegrationTests`) are worth supporting for automatic `IsTestProject` detection in Directory.Build.props.

### Gaps
- I did not fetch, so did not verify current status, versions, or APIs for: NetArchTest(.Rules / NetArchTest.eNhancedEdition), ArchUnitNET, Microsoft.CodeAnalysis.BannedApiAnalyzers (BannedSymbols.txt), CODEOWNERS conventions, or Microsoft's 1ES templates.
- I did not verify concrete layouts of dotnet/runtime (beyond Arcade), dotnet/aspnetcore, eShop (dotnet/eShop), or Orleans. Per-domain layouts (e.g. `src/<Domain>/<Project>`) are common but unsourced here.
- I did not verify current dotnet/runtime-specific conventions (e.g. `src/libraries/<Lib>/{src,ref,tests}`) in this pass.

## 6. .NET Aspire as a monorepo orchestration pattern; polyglot (.NET + JS) monorepos

### Takeaway
Aspire (renamed from ".NET Aspire" with v13, Nov 2025, requiring the .NET 10 SDK) is now explicitly polyglot. JavaScript/Python are first-class, and since **Aspire 13.4 (June 1, 2026) TypeScript AppHosts are GA** (`apphost.mts`, generated SDK in `.aspire/modules/`). The AppHost is a natural "monorepo dev-loop root" that wires .NET services, JS frontends (npm/pnpm/Yarn/Bun), and containers. A monorepo tool should discover AppHosts and respect Aspire's on-disk conventions.

### Cited Findings
- Aspire 13 was announced Nov 11, 2025. Python and JavaScript became first-class (run, debug, deploy), and the .NET 10 SDK is required. — [InfoQ, Nov 2025](https://www.infoq.com/news/2025/11/dotnet-aspire-13-release); [Visual Studio Magazine](https://visualstudiomagazine.com/articles/2025/11/12/microsoft-releases-aspire-13.aspx); [InfoWorld](https://www.infoworld.com/article/4091418/aspire-13-bolsters-python-javascript-support.html)
- Package `Aspire.Hosting.JavaScript` (13.x) hosts JS apps. — [NuGet: Aspire.Hosting.JavaScript](https://packages.nuget.org/packages/Aspire.Hosting.JavaScript/13.0.1)
- Aspire 13.4 (released June 1, 2026):
  - TypeScript AppHosts are GA alongside C#, using `apphost.mts` plus generated SDK modules in `.aspire/modules/`, with startup validation of TS type-check and compile errors.
  - Aspire Type System (ATS) APIs are GA, so `ASPIREATS001` suppressions can be removed.
  - `AddBunApp`/`addBunApp` added. `PublishAsNpmPackageScript` was renamed to `PublishAsPackageScript`, supporting npm, pnpm, Yarn, and Bun.
  - `aspire init --language typescript` creates a nested `aspire-apphost/` folder and package inside an existing JS/TS app.
  - `aspire ls` lists candidate AppHosts in the current directory (table or JSON).
  - The CLI auto-migrates `.aspire/settings.json` to `aspire.config.json`.
  - The VS Code extension supports workspaces with multiple AppHosts and has parser-backed AppHost detection.
  - pnpm workspace Dockerfile support was added (community contribution).
  - Explicit multi-repo support is not described.
  - [aspire.dev: What's new in 13.4](https://aspire.dev/whats-new/aspire-13-4/)
- Aspire 13.5 docs exist, showing TS AppHost imports from `.aspire/modules` and a parameterless `withTerminal()`. — [aspire.dev: What's new in 13.5](https://aspire.dev/whats-new/aspire-13-5.md)
- Aspire can orchestrate C# file-based apps. — [aspire.dev](https://aspire.dev/integrations/dotnet/csharp-file-based-apps)

### Inferences
- For a .NET monorepo tool: treat AppHost projects (C# `*.AppHost.csproj` or `apphost.mts`/`apphost.ts`) as a distinct project kind. Discover them (like `aspire ls`). Support multiple AppHosts per repo (per domain or product). Gitignore or handle `.aspire/` generated modules. Recognize `aspire.config.json` (replacing `.aspire/settings.json`).
- Polyglot repos: Aspire's support for pnpm workspaces and Bun means JS packages can live in the same tree (e.g. `src/web/` or `apps/`), with the AppHost as the integration point. MSBuild Directory.Build.* files do not affect JS, but `Directory.Build.props` placed above a JS folder will affect any C# file-based app or csproj nested there.
- Aspire's `AppHost -> ProjectReference` graph is effectively a service-level dependency graph a monorepo tool could reuse for "affected services" computations.

### Gaps
- I did not verify the exact Aspire 13.5 release date, Aspire roadmap for .NET 11, or whether Aspire supports referencing services across repos.
- I did not verify JS-side monorepo tooling integration (Nx's .NET plugin, Turborepo with dotnet) in this pass. It is likely covered by other researchers.
