---
title: "What I've learnt about monorepos with .NET"
author: Francis Phan
url: https://www.phan.nz/posts/doing-monorepo-dotnet/
published: 2024-01-21
saved: 2026-10-29
tags: [affected-detection, github-actions, dotnet-affected, hybrid-repo, ide-fidelity]
---

# What I've learnt about monorepos with .NET (Francis Phan, 2024-01-21)

## Summary

Phan's team builds Triage-Plus, a multitenant SaaS product written mostly in C#. It started as a single-solution monorepo. As the number of microservices grew, every change caused every service to be tested and deployed. The goal was to **deploy only what a change actually affects**.

They considered Bazel or Nx, polyrepos, and a hybrid. They rejected Bazel and Nx for four reasons:

1. These tools wrap the dotnet CLI in generic rules or plugins and **lose IDE support, notably Rider debugging**. At their scale compile time wasn't the bottleneck; tests, Docker builds and deploys were.
2. Git doesn't scale indefinitely, though that is far off for them.
3. Some code is open source, and clients deploy their own code. A monorepo can't expose a subset of its code to selected people, so they use separate repos plus Copybara.
4. They don't need cross-language interop. That was Canva's reason for picking Bazel.

**Decision:** a hybrid setup.
- About 97% of the code, the internal C#, stays in one monorepo.
- TypeScript (pnpm) and Python (pipenv) live in their own repos, as do open-source and client code.
- They treat the core problem as a project dependency graph, a directed acyclic graph (DAG), and solve it with **dotnet-affected**.

## CI recipe (GitHub Actions)

1. Run `actions/checkout` with **`fetch-depth: 0`**, because shallow clones break diffing.
2. Run `nrwl/nx-set-shas@v4` to find the last successful commit. They pass `main-branch-name: ${{ github.base_ref }}` plus the workflow file name.
3. Run `dotnet affected -f text traversal --from <base> --to ${{ github.sha }}`, which writes `affected.txt` and `affected.proj`.
4. Handle the exit code. **166 means "nothing affected"**; any other non-zero code is a real error.
5. Set `has_output=true/false` in `$GITHUB_OUTPUT`, gate later steps on it, and run `dotnet test affected.proj`.

The full workflow snippet (MIT-licensed by the author) is in the original post.

## Author's stated limitations

- They call it "not an entirely scalable solution". Full-solution compile time is slowly growing.
- Mixed-language code gets none of the monorepo benefits.
- Copybara-style mirroring adds configuration and maintenance.

## Relevance to our tool

- **Confirms the positioning.** IDE and debugger fidelity was the deciding reason for not adopting Nx or Bazel. Keep `.csproj` as the source of truth and don't replace the IDE workflow.
- **Pain is in tests, Docker builds and deploys, not compiles.** Affected detection and generated CI are worth more than faster compilation for this persona. This matches the report's advice to start with affected detection plus generated CI.
- **Boilerplate to eliminate:**
  - Bash for exit code 166, an empty-file check, and `$GITHUB_OUTPUT` wiring.
  - A dependency on a Node or Nx action (`nx-set-shas`) only to find the last green SHA.

  Our tool should emit GitHub outputs and matrices natively and keep "last green SHA per branch" itself, e.g. as a compare-and-swap ref in the object-store cache (see [build cache notes](../../notes/build-cache-object-store.md)).
- **`fetch-depth: 0` is a cost at scale.** Investigate partial clones (`--filter=blob:none`) or fetching only up to the base SHA.
- **Affected scope goes beyond tests.** Their use case is per-service Docker builds and deploys, so the "affected" output must map to deployables, not just test projects.
- **Hybrid repos are common.** Mirroring public or client subsets (Copybara) is a possible later feature, but low priority.
