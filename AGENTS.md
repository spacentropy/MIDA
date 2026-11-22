# Repository Guidelines

## Communication Rules
**Default: concise.** State facts. Do the action. Move on.
- **Be brief** — short sentences, no filler words, no pleasantries. Get to the point.
- **Be precise** — use exact technical terms. Name the file, line, symbol, error code.
- **Be correct** — verify claims against the code. Don't guess. If uncertain, say so.
- **Explain when asked** — when the user asks "why" or requests detail, give a thorough technical explanation.
  ❌ "Sure! I'd be happy to help you with that. The issue you're experiencing is likely caused by..."
  ❌ "Build failed with some errors related to nullability in the auth module" *(summarized output)*
  ✅ "Bug in auth middleware — token expiry check uses `<` not `<=`. Fix:"

---

> **⚠️ Rule 1:** After making significant changes to any component, module, or infrastructure (especially in test infrastructure, CI/CD, dependencies, or architectural patterns), **remind the user to consider updating the corresponding AGENTS.md file** in that directory to keep the documentation in sync with the codebase.

> **⚠️ Rule 2:** Do not write unnecessary comments in the code. Write readable code.

## Tools
- C# → Roslynk; file/text search in the current repo → fff; fff down or a checkout other than the session's launch one → Read + `rg`/`fd` on absolute paths; elsewhere `rg`/`fd`; never `grep`/`find`.
- Use CLI JSON output when supported; process JSON with `jaq` (never `jq`); pipe JSON you read through `| toon` (not JSON fed to another tool).
- C# (.cs/.razor/.cshtml): use Roslynk.
- Roslynk writes bypass `/rewind`: if `git status` is dirty, ask before making them; review `git diff` after.
- After a semantic rename or signature change, search the original name repo-wide (other language, config, SQL, snapshots); fix by hand.
- After Roslynk writes, a branch switch or codegen, LSP diagnostics may be stale: trust Roslynk `get_diagnostics` (`includeAnalyzers=false` for speed) over them; on `watchDiagnostic=` or results disagreeing with disk, suggest `reload_solution`.
- After `dotnet restore`, call Roslynk `reload_solution` yourself (its watcher ignores obj/).

## Where things run

- GitHub-hosted runners only build releases (`release.yml`), run the tests (`ci.yml`) and do chores: submitting a job to the Cloud Build pool and collecting what it leaves, code scanning, uploading artifacts. Everything heavier runs on the pool through `.github/actions/pool`.

## Naming other projects

- mida is a general-purpose build tool. Code, comments, test and script names, the README, `docs/notes` and `docs/plan.md` never name another open-source repo or library, not even the one that showed a bug. Describe the problem itself, such as "a test project that makes `bin/.../wwwroot` for a web host it starts", and a result by what the repo is, such as "a web CMS of 227 projects".
- Exceptions: the real-world harness (`tests/Mida.Tests/RealWorld.cs`, `.github/workflows/realworld.yml`) names the repos it clones, and `docs/research_notes` surveys other tools on purpose. The platform and the tools mida is built on or supports, such as .NET, MSBuild, NuGet, ASP.NET Core, TUnit and BenchmarkDotNet, may be named.

## Benchmarks

- Every benchmarking run, meaning any run whose times are reported, compared or written into the docs, runs on Google Cloud Build, through the pool in `.github/actions/pool`, as `.github/workflows/bench.yml` does. The pool, `mida-8`, has 8 processors and a disk that writes about 205 MB/s, and it is the machine to optimize for. Times are taken with BenchmarkDotNet (`tests/Mida.Tests/Timed.cs`).
- Locally, run benchmarks and the `RealWorld` tests only to check correctness: hits and misses, which tests ran, and the same files as `dotnet build`. The times a local run prints are not results, so do not report them, compare them or write them down. A laptop shares its processors with everything else on it, and one run on a loaded Mac put the same plain build anywhere from 10.9 s to 25.4 s.
