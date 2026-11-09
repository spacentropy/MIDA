using System.Collections.Concurrent;

namespace Mida.Store;

/// <summary>A remote store behind a local one. Everything read from remote lands in local first, so every hit restores by cloning.</summary>
public sealed class TieredStore(IObjectStore local, IObjectStore remote) : IObjectStore
{
    private readonly ConcurrentDictionary<string, Lazy<Task<byte[]?>>> fetches = new(StringComparer.Ordinal);

    // Local only once remote took it: a manifest another writer won must reach local from remote, or it could not be compared.
    public async Task<bool> TryCreateAsync(string key, ReadOnlyMemory<byte> content, CancellationToken cancellationToken) =>
        await remote.TryCreateAsync(key, content, cancellationToken) && await local.TryCreateAsync(key, content, cancellationToken);

    public async Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken)
    {
        if (await local.GetAsync(key, cancellationToken) is { } content)
        {
            return content;
        }

        return await FetchAsync(key, cancellationToken);
    }

    public async Task<bool> TryGetFileAsync(string key, string destinationFile, CancellationToken cancellationToken)
    {
        if (await local.TryGetFileAsync(key, destinationFile, cancellationToken))
        {
            return true;
        }

        return await FetchAsync(key, cancellationToken) is not null && await local.TryGetFileAsync(key, destinationFile, cancellationToken);
    }

    /// <summary>
    /// One download per key: on a fresh runner many projects want the same blob at once. It runs on no caller's token,
    /// which ends when any file of that caller's restore fails. Removed when done, so a failure is not reused.
    /// </summary>
    private async Task<byte[]?> FetchAsync(string key, CancellationToken cancellationToken)
    {
        var fetch = fetches.GetOrAdd(key, k => new Lazy<Task<byte[]?>>(() => DownloadAsync(k)));
        try
        {
            return await fetch.Value.WaitAsync(cancellationToken);
        }
        finally
        {
            fetches.TryRemove(new KeyValuePair<string, Lazy<Task<byte[]?>>>(key, fetch));
        }
    }

    private async Task<byte[]?> DownloadAsync(string key)
    {
        if (await remote.GetAsync(key, CancellationToken.None) is not { } fetched)
        {
            return null;
        }

        await local.TryCreateAsync(key, fetched, CancellationToken.None);
        return fetched;
    }
}
