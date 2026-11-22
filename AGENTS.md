# Rules for agents

## Where things run

- GitHub-hosted runners only build releases (`release.yml`), run the tests (`ci.yml`) and do chores: submitting a job to the Cloud Build pool and collecting what it leaves, code scanning, uploading artifacts. Everything heavier runs on the pool through `.github/actions/pool`.

## Naming other projects

- mida is a general-purpose build tool. Code, comments, test and script names, the README, `docs/notes` and `docs/plan.md` never name another open-source repo or library, not even the one that showed a bug. Describe the problem itself, such as "a test project that makes `bin/.../wwwroot` for a web host it starts", and a result by what the repo is, such as "a web CMS of 227 projects".
- Exceptions: the real-world harness (`tests/Mida.Tests/RealWorld.cs`, `.github/workflows/realworld.yml`) names the repos it clones, and `docs/research_notes` surveys other tools on purpose. The platform and the tools mida is built on or supports, such as .NET, MSBuild, NuGet, ASP.NET Core, TUnit and BenchmarkDotNet, may be named.

## Benchmarks

- Every benchmarking run, meaning any run whose times are reported, compared or written into the docs, runs on Google Cloud Build, through the pool in `.github/actions/pool`, as `.github/workflows/bench.yml` does. The pool, `mida-8`, has 8 processors and a disk that writes about 205 MB/s, and it is the machine to optimize for. Times are taken with BenchmarkDotNet (`tests/Mida.Tests/Timed.cs`).
- Locally, run benchmarks and the `RealWorld` tests only to check correctness: hits and misses, which tests ran, and the same files as `dotnet build`. The times a local run prints are not results, so do not report them, compare them or write them down. A laptop shares its processors with everything else on it, and one run on a loaded Mac put the same plain build anywhere from 10.9 s to 25.4 s.
