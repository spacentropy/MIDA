namespace Mida.Store;

public sealed class FileSystemStore(string root) : IObjectStore
{
    public Task<bool> TryCreateAsync(string key, ReadOnlyMemory<byte> content, CancellationToken cancellationToken) =>
        CreateAsync(key, temp => File.WriteAllBytesAsync(temp, content, cancellationToken));

    public async Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken)
    {
        var path = PathOf(key);
        return File.Exists(path) ? await File.ReadAllBytesAsync(path, cancellationToken) : null;
    }

    public Task<bool> TryGetFileAsync(string key, string destinationFile, CancellationToken cancellationToken)
    {
        var path = PathOf(key);
        if (!File.Exists(path))
        {
            return Task.FromResult(false);
        }

        // Clones where the filesystem can: 2 GB took 0 ms on APFS.
        File.Copy(path, destinationFile, overwrite: true);
        return Task.FromResult(true);
    }

    private string PathOf(string key) => StorePath.Contained(root, key);

    private async Task<bool> CreateAsync(string key, Func<string, Task> write)
    {
        var path = PathOf(key);
        if (File.Exists(path))
        {
            return false;
        }

        // Written aside, so a writer that dies leaves nothing a reader could take for a whole object.
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            await write(temp);

            // shortcut: File.Move checks, then renames, so racing writers can both report created. The object stays whole
            // and either writer's is valid. Upgrade if a local store ever has to detect a second writer.
            File.Move(temp, path, overwrite: false);
            return true;
        }
        catch (IOException) when (File.Exists(path))
        {
            return false;
        }
        finally
        {
            File.Delete(temp);
        }
    }
}
