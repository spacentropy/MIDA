using System.Text;
using System.Text.Json;
using Mida.Store;

namespace Mida.Tests;

public sealed class OutputCacheTests : IDisposable
{
    private const string Key = "key";

    private readonly string temp = Directory.CreateTempSubdirectory("mida-tests-").FullName;
    private readonly FileSystemStore store;
    private readonly string repo;

    public OutputCacheTests()
    {
        store = new FileSystemStore(Path.Combine(temp, "cache"));
        repo = Path.Combine(temp, "repo");
    }

    private static CancellationToken Token => TestContext.Current!.Execution.CancellationToken;

    public void Dispose() => Directory.Delete(temp, recursive: true);

    private static string InRepo(string root, string relative, string content)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private Task Save(params string[] files) =>
        new OutputCache(store, repo).SaveAsync(Key, files, [], [], Token);

    private string Blob(string hash) => Path.Combine(temp, "cache", "v1", "cas", hash[..2], hash);

    [Test]
    public async Task RestoresFilesModesAndReferencesAtAnotherRoot()
    {
        var file = InRepo(repo, "App/bin/app", "app");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        ManifestTarget[] targets = [new("Build", [new ManifestItem("{RepoRoot}/App/bin/app", new Dictionary<string, string> { ["A"] = "b" })])];
        await new OutputCache(store, repo).SaveAsync(Key, [file], [new ManifestReference("App/bin/lib.dll", "Lib/bin/lib.dll")], targets, Token);

        var other = Path.Combine(temp, "other");
        InRepo(other, "Lib/bin/lib.dll", "current");
        var manifest = await new OutputCache(store, other).TryRestoreAsync(Key, Token);

        Assert.NotNull(manifest);
        await Assert.That(manifest.Targets[0].Items[0].Spec).IsEqualTo("{RepoRoot}/App/bin/app");
        await Assert.That(manifest.Targets[0].Items[0].Metadata["A"]).IsEqualTo("b");
        var restored = Path.Combine(other, "App", "bin", "app");
        await Assert.That(File.ReadAllText(restored)).IsEqualTo("app");
        await Assert.That(File.ReadAllText(Path.Combine(other, "App", "bin", "lib.dll"))).IsEqualTo("current");
        if (!OperatingSystem.IsWindows())
        {
            await Assert.That(File.GetUnixFileMode(restored).HasFlag(UnixFileMode.UserExecute)).IsTrue();
        }
    }

    [Test]
    public async Task OverwritesAReadOnlyFile()
    {
        await Save(InRepo(repo, "bin/a", "new"));

        var other = Path.Combine(temp, "other");
        var existing = InRepo(other, "bin/a", "old");
        File.SetAttributes(existing, FileAttributes.ReadOnly);

        await new OutputCache(store, other).TryRestoreAsync(Key, Token);

        await Assert.That(File.ReadAllText(existing)).IsEqualTo("new");
    }

    [Test]
    public async Task UnknownKeyIsAMiss() =>
        await Assert.That(await new OutputCache(store, repo).TryRestoreAsync(Key, Token)).IsNull();

    [Test]
    public async Task MissingBlobThrows()
    {
        var file = InRepo(repo, "bin/a", "a");
        await Save(file);
        File.Delete(Blob(OutputCache.Hash(file)));

        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => new OutputCache(store, repo).TryRestoreAsync(Key, Token));
    }

    [Test]
    public async Task CorruptBlobThrowsAndLeavesNoFileBehind()
    {
        var file = InRepo(repo, "bin/a", "a");
        await Save(file);
        File.WriteAllText(Blob(OutputCache.Hash(file)), "tampered");

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => new OutputCache(store, repo).TryRestoreAsync(Key, Token));

        await Assert.That(File.Exists(file)).IsFalse();
    }

    [Test]
    public async Task FailedBlobCheckDoesNotStickForTheRun()
    {
        var file = InRepo(repo, "bin/a", "a");
        await Save(file);
        var blob = Blob(OutputCache.Hash(file));
        File.WriteAllText(blob, "tampered");
        var cache = new OutputCache(store, repo);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => cache.TryRestoreAsync(Key, Token));

        File.WriteAllText(blob, "a");

        await Assert.That(await cache.TryRestoreAsync(Key, Token)).IsNotNull();
        await Assert.That(File.ReadAllText(file)).IsEqualTo("a");
    }

    [Test]
    [Arguments("""{"files":[{"path":"../evil","hash":"HASH","mode":null}],"references":[],"targets":[]}""")]
    [Arguments("""{"files":[],"references":[{"path":"../evil","source":"a"}],"targets":[]}""")]
    [Arguments("""{"files":[],"references":[{"path":"a","source":"../../secret"}],"targets":[]}""")]
    [Arguments("""{"files":[{"path":"a","hash":"../../../secret","mode":null}],"references":[],"targets":[]}""")]
    public async Task ManifestCannotReachOutsideTheRepo(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        File.WriteAllText(Path.Combine(temp, "secret"), "secret");

        await AssertRejectedWithOutputsUntouched<InvalidDataException>(json);

        await Assert.That(File.Exists(Path.Combine(temp, "evil"))).IsFalse();
    }

    [Test]
    [Arguments("""{"files":[{"path":"a","hash":"a","mode":null}],"references":[],"targets":[]}""")]
    [Arguments("""{"files":[{"path":"a","hash":"HASHZ","mode":null}],"references":[],"targets":[]}""")]
    [Arguments("""{"files":[{"path":"a","hash":"HASH","mode":-1}],"references":[],"targets":[]}""")]
    [Arguments("""{"files":[{"path":"a","hash":"HASH","mode":4096}],"references":[],"targets":[]}""")]
    [Arguments("""{"files":[{"path":"a\u0000b","hash":"HASH","mode":null}],"references":[],"targets":[]}""")]
    [Arguments("""{"files":[{"path":"","hash":"HASH","mode":null}],"references":[],"targets":[]}""")]
    [Arguments("""{"files":[{"path":"a","hash":"HASH","mode":null},{"path":"a","hash":"HASH","mode":null}],"references":[],"targets":[]}""")]
    [Arguments("""{"files":[{"path":"a","hash":"HASH","mode":null}],"references":[{"path":"a","source":"b"}],"targets":[]}""")]
    [Arguments("""{"files":[{"path":"bin/a","hash":"HASH","mode":null},{"path":"bin/a/b","hash":"HASH","mode":null}],"references":[],"targets":[]}""")]
    [Arguments("""{"files":[null],"references":[],"targets":[]}""")]
    [Arguments("""{"files":[],"references":[null],"targets":[]}""")]
    [Arguments("""{"files":[],"references":[],"targets":[null]}""")]
    [Arguments("""{"files":[],"references":[],"targets":[{"name":"","items":[]}]}""")]
    [Arguments("""{"files":[],"references":[],"targets":[{"name":"Build","items":[]},{"name":"build","items":[]}]}""")]
    [Arguments("""{"files":[],"references":[],"targets":[{"name":"Build","items":[null]}]}""")]
    [Arguments("""{"files":[],"references":[],"targets":[{"name":"Build","items":[{"spec":"s","metadata":{"a":null}}]}]}""")]
    public async Task MalformedEntryIsRejectedBeforeAnyOutputChanges(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        await AssertRejectedWithOutputsUntouched<InvalidDataException>(json);
    }

    [Test]
    [Arguments("""{}""")]
    [Arguments("""{"files":null,"references":[],"targets":[]}""")]
    [Arguments("""{"files":[],"references":null,"targets":[]}""")]
    [Arguments("""{"files":[],"references":[],"targets":null}""")]
    [Arguments("""{"files":[{"path":null,"hash":"HASH","mode":null}],"references":[],"targets":[]}""")]
    [Arguments("""{"files":[{"path":"a","hash":null,"mode":null}],"references":[],"targets":[]}""")]
    [Arguments("""{"files":[],"references":[{"path":null,"source":"a"}],"targets":[]}""")]
    [Arguments("""{"files":[],"references":[],"targets":[{"name":"Build","items":null}]}""")]
    [Arguments("""{"files":[],"references":[],"targets":[{"name":"Build","items":[{"spec":"s","metadata":null}]}]}""")]
    public async Task NullPropertyIsRejectedBeforeAnyOutputChanges(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        await AssertRejectedWithOutputsUntouched<JsonException>(json);
    }

    private async Task AssertRejectedWithOutputsUntouched<TException>(string json)
        where TException : Exception
    {
        var existing = InRepo(repo, "a", "existing");
        var manifest = json.Replace("HASH", new string('a', 64), StringComparison.Ordinal);
        await store.TryCreateAsync("v1/ac/" + Key, Encoding.UTF8.GetBytes(manifest), Token);

        await Assert.ThrowsExactlyAsync<TException>(() => new OutputCache(store, repo).TryRestoreAsync(Key, Token));

        await Assert.That(File.ReadAllText(existing)).IsEqualTo("existing");
    }

    [Test]
    public async Task CreateOnlyKeepsTheFirstWriter()
    {
        await Assert.That(await store.TryCreateAsync("k", "first"u8.ToArray(), Token)).IsTrue();
        await Assert.That(await store.TryCreateAsync("k", "second"u8.ToArray(), Token)).IsFalse();

        await Assert.That(Encoding.UTF8.GetString((await store.GetAsync("k", Token))!)).IsEqualTo("first");
    }

    [Test]
    public async Task ConcurrentWritersLeaveOneWholeObject()
    {
        var content = Enumerable.Range(0, 32).Select(i => new string((char)('a' + (i % 26)), 100_000)).ToList();

        var results = await Task.WhenAll(content.Select(c =>
            Task.Run(() => store.TryCreateAsync("k", Encoding.UTF8.GetBytes(c), Token), Token)));

        await Assert.That(results).Contains(true);
        await Assert.That(content).Contains(Encoding.UTF8.GetString((await store.GetAsync("k", Token))!));
        var directory = Path.Combine(temp, "cache");
        await Assert.That(Directory.GetFiles(directory)).IsEquivalentTo([Path.Combine(directory, "k")]);
    }
}
