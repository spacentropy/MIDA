namespace Mida.Store;

/// <summary>A restore goes to a path so a filesystem store can clone, which is what makes a local hit cheap.</summary>
public interface IObjectStore
{
    /// <summary>Create-only. False means the key already exists, which is success for content-addressed data.</summary>
    Task<bool> TryCreateAsync(string key, ReadOnlyMemory<byte> content, CancellationToken cancellationToken);

    Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken);

    /// <summary>False means the key does not exist.</summary>
    Task<bool> TryGetFileAsync(string key, string destinationFile, CancellationToken cancellationToken);
}
