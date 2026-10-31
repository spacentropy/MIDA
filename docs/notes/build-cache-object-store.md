# Build cache on object storage

Status: design notes, 2026-10-29. Context: [the landscape report](../research_notes/reports/NET%20monorepo%20tooling%20landscape.md) ranks a relocatable, storage-agnostic remote cache as opportunity #1.

## Decision

The remote build cache lives in **plain object storage**. There is no cache server to run. The tool talks to the bucket directly through a small storage abstraction with one implementation per provider.

- **First implementation: Google Cloud Storage (GCS).**
- Then S3 (and S3-compatible stores), Azure Blob, and a local filesystem backend for tests and single-machine use.
- Every backend must use the provider's native concurrency primitives (create-only writes, compare-and-swap on a version token). There is no external lock service.

Why object storage:
- Teams already have a bucket, IAM, and billing.
- Storage is cheap and durable, and no service needs operating.
- All three major providers now offer strongly consistent conditional writes, which is all a cache needs.

It also avoids MSBuildCache's weaknesses: it is tied to Azure, and its Blob backend has no trust model.

## Data model

Two namespaces, as in Bazel's remote cache: content-addressed blobs and action-cache entries.

```
<prefix>/v1/cas/<sha256[0:2]>/<sha256>        immutable output blobs (zstd)
<prefix>/v1/ac/<cache-key>                    manifest: outputs -> CAS digests + metadata
<prefix>/v1/refs/<name>                       small mutable pointers (e.g. last green SHA per branch)
<prefix>/v1/pr/<number>/ac/<cache-key>        optional PR-scoped writable namespace
```

- **CAS blobs** are write-once and keyed by their own hash. A collision means identical content, so "already exists" counts as success.
- **AC entries** map a task's input hash (MSBuild-evaluated inputs plus dependency reference-assembly hashes; see report §"Why .NET outputs resist caching") to a manifest. The manifest lists output paths relative to the project, CAS digests, file modes, tool and SDK versions, and the producer (commit, CI run).
- AC entries are also **first-writer-wins**. A second writer with a *different* manifest for the same key is a nondeterminism signal. Log it and surface it, but don't overwrite.
- **Refs** are the only mutable objects and are updated with compare-and-swap. The first use case is "last successful commit per branch". This replaces `nrwl/nx-set-shas` in CI (see [Phan article](../research_notes/articles/2024-01-21-phan-monorepos-with-dotnet.md)), so no Node or Nx is needed.

Write order: CAS blobs first, then the AC manifest. A reader that finds a manifest can rely on its blobs existing, provided the garbage-collection (GC) rules below hold.

## Storage abstraction

```csharp
interface IObjectStore
{
    // Create-only. Returns AlreadyExists instead of throwing on a precondition failure.
    Task<CreateResult> TryCreateAsync(string key, Stream content, ObjectMetadata meta, CancellationToken ct);

    Task<StoredObject?> GetAsync(string key, CancellationToken ct);        // content + VersionToken
    Task<ObjectInfo?>   HeadAsync(string key, CancellationToken ct);

    // Compare-and-swap. expected == null means "must not exist".
    Task<SwapResult> TrySwapAsync(string key, VersionToken? expected, Stream content, CancellationToken ct);

    Task<bool> TryDeleteAsync(string key, VersionToken expected, CancellationToken ct);
    IAsyncEnumerable<ObjectInfo> ListAsync(string prefix, CancellationToken ct);
}
```

`VersionToken` is opaque: a GCS generation number, or an ETag on S3 and Azure. Precondition failures are normal return values, not exceptions.

## Provider primitives (verified against official docs, 2026-10-29)

| Need | GCS | S3 | Azure Blob |
|---|---|---|---|
| Create-only write | `ifGenerationMatch=0` (JSON) / `x-goog-if-generation-match: 0` (XML) | `If-None-Match: *` on PutObject, CompleteMultipartUpload, CopyObject | `If-None-Match: *` on Put Blob, Put Block List, Copy Blob |
| Compare-and-swap | `ifGenerationMatch=<gen>` (+ `ifMetagenerationMatch` for metadata) | `If-Match: <etag>` (needs `s3:GetObject` as well as `s3:PutObject`) | `If-Match: <etag>` |
| Failure signal | 412 | 412; 409 on a race with a concurrent delete | 412 |
| Conditional delete | generation precondition on delete | (not covered in the page read) | `If-Match` on Delete Blob |
| Server-side enforcement | (IAM only) | Bucket policy can *require* conditional writes | (not covered) |
| Tag-conditioned ops | n/a | n/a | `x-ms-if-tags` predicate |

Provider-specific details that shape the implementation:

**GCS (first target)**
- Prefer generation and metageneration over ETags. Google's docs say generation numbers are "consistent across APIs, whereas ETags are not."
- If using a metageneration precondition, always pair it with a generation precondition.
- `ifGenerationMatch=0` also makes upload *retries* safe: a retried upload can't write the object twice. Retry on 5xx and timeouts; treat 412 on a CAS create as success.
- **XML API multipart uploads don't support preconditions** (`400 NotImplemented`). For large blobs, use JSON API resumable uploads with the precondition set when the upload is started. Verify this end to end.
- **Compose** supports a destination generation precondition and a per-source generation match. This allows parallel composite uploads of large outputs that are safe against a component being overwritten mid-compose.
- Fetching the current generation costs an extra round trip. Cache generation numbers locally (e.g. for refs) and use them directly in the next swap, re-reading only on 412.

**S3**
- Concurrent `If-None-Match` writes: "the first write operation to finish succeeds", and the rest get 412.
- A 409 can occur if a delete wins the race. PutObject can be retried after 409; CompleteMultipartUpload must restart from `CreateMultipartUpload`.
- Conditional writes ignore in-progress multipart uploads. A CAS blob uploaded via MPU can lose to a concurrent PutObject; that's fine, because the content is identical.
- Requires SigV4.
- Add a lifecycle rule `AbortIncompleteMultipartUpload` so abandoned uploads don't accrue cost.
- With versioning enabled, `If-None-Match` checks only the current version. A delete marker counts as absent.
- Use a bucket policy that **enforces** conditional writes on `cas/` and `ac/`, so no client, buggy or malicious, can overwrite an entry.

**Azure Blob**
- `If-None-Match: *` on Put Blob / Put Block List for create-only writes, and `If-Match: <etag>` for compare-and-swap. Write operations allow only one conditional header, except the documented pairs.
- `Put Block` (staging) doesn't support conditions. The condition goes on `Put Block List` (commit).
- `x-ms-if-tags` allows conditioning on blob index tags, a possible hook for "only overwrite if tagged untrusted".

**S3-compatible (R2, MinIO, Ceph, …)**
- Conditional-write support varies. Probe it at startup and refuse to run in write mode if create-only can't be guaranteed.

## Trust model

This is the main fix over MSBuildCache, whose README warns that its Blob backend is unsafe with untrusted PRs.

- **Trusted writers only** write `cas/`, `ac/` and `refs/`. These are CI jobs on protected branches, authenticated by OIDC: GCS Workload Identity Federation, an AWS IAM role trust on the token `sub`, or Azure federated credentials, all scoped to a repo and ref.
- **PR and fork builds read only.** Optionally they get write access to `pr/<number>/`, which only that PR reads. PR caches can never leak into main.
- Developer machines read only by default. Downloads can use short-lived signed URLs (GCS signed URLs / S3 presigned / Azure SAS), so laptops need no long-lived cloud credentials.
- Verify the SHA-256 of every downloaded CAS blob before materializing it. Never trust the store.

## Garbage collection

- Rely on provider lifecycle rules, not a GC daemon.
- Invariant: **a CAS blob must outlive every AC entry that references it.** The simplest way is a longer TTL on `cas/` than on `ac/`. A smarter option is "touch on hit", bumping a recency marker throttled to about once a day per key.
  - To verify: GCS `customTime` metadata with the `daysSinceCustomTime` lifecycle condition. Azure lifecycle `daysAfterLastAccessTimeGreaterThan` with access tracking enabled. S3 has no last-access lifecycle condition (age only).
- A manifest whose blob is missing is treated as a cache miss and reported, not as an error.

## Open questions

- Compression: zstd per blob, or pack small outputs (obj/ fragments) into one archive per task to cut request count? Request cost, not bytes, likely dominates for .NET's many small files.
- Should a narrow subset of Bazel's remote cache HTTP protocol be supported as an extra backend, so existing BuildBuddy and EngFlow-style caches can be reused?
- Multi-region and read-latency: is a local disk tier needed in front of the bucket (yes, almost certainly), and how is it evicted?
- How to unit-test concurrency semantics: a fake store that simulates races, plus integration tests against real buckets and emulators (fake-gcs-server, MinIO, Azurite). Check how faithfully each emulator implements preconditions.

## Sources

- GCS request preconditions: https://docs.cloud.google.com/storage/docs/request-preconditions
- S3 conditional writes: https://docs.aws.amazon.com/AmazonS3/latest/userguide/conditional-writes.html
- Azure Blob conditional headers: https://learn.microsoft.com/en-us/rest/api/storageservices/specifying-conditional-headers-for-blob-service-operations
