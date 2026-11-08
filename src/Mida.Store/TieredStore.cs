namespace Mida.Store;

/// <summary>A remote store behind a local one. Everything read from remote lands in local first, so every hit restores by cloning.</summary>
public sealed class TieredStore(IObjectStore local, IObjectStore remote) : IObjectStore
{
    // Local only once remote took it: a manifest another writer won must reach local from remote, or it could not be compared.
    public async Task<bool> TryCreateAsync(string key, ReadOnlyMemory<byte> content, CancellationToken cancellationToken) =>
        await remote.TryCreateAsync(key, content, cancellationToken) && await local.TryCreateAsync(key, content, cancellationToken);

    public async Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken)
    {
        if (await local.GetAsync(key, cancellationToken) is { } content)
        {
            return content;
        }

        if (await remote.GetAsync(key, cancellationToken) is not { } fetched)
        {
            return null;
        }

        await local.TryCreateAsync(key, fetched, cancellationToken);
        return fetched;
    }

    public async Task<bool> TryGetFileAsync(string key, string destinationFile, CancellationToken cancellationToken)
    {
        if (await local.TryGetFileAsync(key, destinationFile, cancellationToken))
        {
            return true;
        }

        if (await remote.GetAsync(key, cancellationToken) is not { } fetched)
        {
            return false;
        }

        await local.TryCreateAsync(key, fetched, cancellationToken);
        return await local.TryGetFileAsync(key, destinationFile, cancellationToken);
    }
}
