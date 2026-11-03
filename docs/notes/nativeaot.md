# NativeAOT: deferred, door kept open

Status: decision, 2026-11-01. Referenced from [design §1.6](../design.md#16-constraints-we-hold-to), which holds the rules that keep the AOT path open.

## Decision

v1 ships framework-dependent and uses MSBuildLocator. NativeAOT is revisited when the .NET SDK's workload and NuGet SDK resolvers are trim/AOT-safe and can be registered in-process (`SdkResolver.Register`), or when tool startup becomes the dominant cost of a cache-hit run.

## What was verified (2026-11-01, osx-arm64, SDK 11.0.100-rc.3, MSBuild 18.12)

- Microsoft.Build is `IsAotCompatible` from 18.10 ([dotnet/msbuild #14064](https://github.com/dotnet/msbuild/pull/14064), July 2026). The motivating consumer is an AOT-compiled `dotnet` CLI. Evaluation and `ProjectGraph` work in a NativeAOT host with a bundled engine. Build execution does not: tasks load by reflection, so the tool shells out to `dotnet build` regardless.
- An AOT host cannot use MSBuildLocator, which works by JIT plus `Assembly.LoadFrom`. It must bundle Microsoft.Build, Microsoft.Build.Utilities.Core and NuGet.Frameworks, and the bundled versions must match the SDK's. Microsoft.Build 18.11 wanted NuGet.Frameworks 7.10.0.0, which was not on NuGet.org, so the bundled JIT variant failed outright.
- Plugin SDK resolvers do not load under AOT. Workloads had to be disabled with `MSBuildEnableWorkloadResolver=false`, and a Microsoft.Build.Traversal `dirs.proj` failed. MSBuild's intended fix is in-process registration ([host registration spec](https://github.com/dotnet/msbuild/blob/main/documentation/specs/sdk-resolver-host-registration-api.md)), but the workload and NuGet resolvers themselves are not AOT-safe yet.
- Project-cache plugin loading is not yet behind a feature switch ([follow-up work](https://github.com/dotnet/msbuild/blob/main/documentation/aot/follow-up-work.md)).

## Measured

| Scenario | Framework-dependent | NativeAOT |
|---|---|---|
| Installed tool start through the `dotnet tool` shim | 20 ms | 5 ms |
| Evaluate one project + `ProjectGraph`, whole process | 255 ms (Locator) | 107 ms |
| `ProjectGraph` of 50 chained projects, whole process | 875 ms (Locator) | 485 ms |
| `dotnet msbuild -getProperty` on one project, the shell-out alternative | 575 ms | – |

The gap is mostly fixed JIT warmup, so it stays near 400 ms as the repo grows. The AOT runs had workloads disabled and the JIT runs did not, which slightly flatters AOT. An AOT tool is 14 MB per RID and needs one native build job per OS plus a framework-dependent fallback package ([RID-specific tools](https://learn.microsoft.com/en-us/dotnet/core/tools/rid-specific-tools)).

## Sources

- [MSBuild AOT strategy](https://github.com/dotnet/msbuild/blob/main/documentation/aot/aot-trimming-strategy.md), [SDK resolution under AOT](https://github.com/dotnet/msbuild/blob/main/documentation/aot/sdk-resolution.md)
- [MSBuildLocator](https://github.com/microsoft/msbuildlocator)
- [Native AOT deployment overview](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)
