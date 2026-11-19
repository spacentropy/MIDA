# Rules for agents

## Where things run

- GitHub-hosted runners only build releases (`release.yml`), run the tests (`ci.yml`) and do chores: submitting a job to the Cloud Build pool and collecting what it leaves, code scanning, uploading artifacts. Everything heavier runs on the pool through `.github/actions/pool`.

## Benchmarks

- Every benchmarking run, meaning any run whose times are reported, compared or written into the docs, runs on Google Cloud Build, through the pool in `.github/actions/pool`, as `.github/workflows/bench.yml` does. The target is the `mida-8` pool, 8 processors and a disk that writes about 205 MB/s: optimize for it, and use the 22-processor `mida` pool only to compare. Times are taken with BenchmarkDotNet (`tests/Mida.Tests/Timed.cs`).
- Locally, run benchmarks and the `RealWorld` tests only to check correctness: hits and misses, which tests ran, and the same files as `dotnet build`. The times a local run prints are not results, so do not report them, compare them or write them down. A laptop shares its processors with everything else on it, and one run on a loaded Mac put the same plain build anywhere from 10.9 s to 25.4 s.
