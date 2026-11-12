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

    private Task<bool> Save(params string[] files) =>
        new OutputCache(store, repo).SaveAsync(Key, files, [], [], TimeSpan.Zero, Token);

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
        await new OutputCache(store, repo).SaveAsync(Key, [file], [new ManifestReference("App/bin/lib.dll", "Lib/bin/lib.dll")], targets, TimeSpan.Zero, Token);

        var other = Path.Combine(temp, "other");
        InRepo(other, "Lib/bin/lib.dll", "current");
        var manifest = await new OutputCache(store, other).TryRestoreAsync(Key, null, Token);

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

    private static Relocation Swap(string from, string to) => new(
        (path, content) => path.EndsWith(".json", StringComparison.Ordinal) ? Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(content).Replace(from, "{R}", StringComparison.Ordinal)) : null,
        content => Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(content).Replace("{R}", to, StringComparison.Ordinal)));

    [Test]
    public async Task MarkedFileIsStoredWithTokensAndRestoredWithThisCheckoutsPaths()
    {
        var other = Path.Combine(temp, "other");
        var json = InRepo(repo, "bin/a.json", $"[\"{repo}/wwwroot\"]");
        var plain = InRepo(repo, "bin/b", repo);
        await new OutputCache(store, repo, Swap(repo, "unused")).SaveAsync(Key, [json, plain], [], [], TimeSpan.Zero, Token);

        var manifest = await new OutputCache(store, other, Swap("unused", other)).TryRestoreAsync(Key, null, Token);

        Assert.NotNull(manifest);
        await Assert.That(manifest.Files.Select(f => f.Tokenized)).IsEquivalentTo([true, false]);
        await Assert.That(File.ReadAllText(Blob(manifest.Files[0].Hash))).IsEqualTo("[\"{R}/wwwroot\"]");
        await Assert.That(File.ReadAllText(Path.Combine(other, "bin", "a.json"))).IsEqualTo($"[\"{other}/wwwroot\"]");
        await Assert.That(File.ReadAllText(Path.Combine(other, "bin", "b"))).IsEqualTo(repo);
    }

    [Test]
    public async Task ManifestWrittenBeforeTheTokenMarkRestores()
    {
        var hash = await new OutputCache(store, repo).SaveBlobAsync("a"u8.ToArray(), Token);
        await store.TryCreateAsync("v1/ac/" + Key, Encoding.UTF8.GetBytes($$"""{"files":[{"path":"bin/a","hash":"{{hash}}","mode":null}],"references":[],"targets":[]}"""), Token);

        var manifest = await new OutputCache(store, repo, Swap("a", "b")).TryRestoreAsync(Key, null, Token);

        await Assert.That(manifest?.Files[0].Tokenized).IsFalse();
        await Assert.That(File.ReadAllText(Path.Combine(repo, "bin", "a"))).IsEqualTo("a");
    }

    [Test]
    public async Task OverwritesAReadOnlyFile()
    {
        await Save(InRepo(repo, "bin/a", "new"));

        var other = Path.Combine(temp, "other");
        var existing = InRepo(other, "bin/a", "old");
        File.SetAttributes(existing, FileAttributes.ReadOnly);

        await new OutputCache(store, other).TryRestoreAsync(Key, null, Token);

        await Assert.That(File.ReadAllText(existing)).IsEqualTo("new");
    }

    [Test]
    public async Task UnknownKeyIsAMiss() =>
        await Assert.That(await new OutputCache(store, repo).TryRestoreAsync(Key, null, Token)).IsNull();

    [Test]
    public async Task MissingBlobThrows()
    {
        var file = InRepo(repo, "bin/a", "a");
        await Save(file);
        File.Delete(Blob(OutputCache.Hash(file)));

        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => new OutputCache(store, repo).TryRestoreAsync(Key, null, Token));
    }

    [Test]
    public async Task CorruptBlobThrowsAndLeavesNoFileBehind()
    {
        var file = InRepo(repo, "bin/a", "a");
        await Save(file);
        File.WriteAllText(Blob(OutputCache.Hash(file)), "tampered");

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => new OutputCache(store, repo).TryRestoreAsync(Key, null, Token));

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
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => cache.TryRestoreAsync(Key, null, Token));

        File.WriteAllText(blob, "a");

        await Assert.That(await cache.TryRestoreAsync(Key, null, Token)).IsNotNull();
        await Assert.That(File.ReadAllText(file)).IsEqualTo("a");
    }

    [Test]
    public async Task SecondWriterWithDifferentOutputsIsReportedAndLosesTheKey()
    {
        var file = InRepo(repo, "bin/a", "first");
        await Assert.That(await Save(file)).IsTrue();
        await Assert.That(await Save(file)).IsTrue();

        File.WriteAllText(file, "second");
        await Assert.That(await Save(file)).IsFalse();

        await new OutputCache(store, repo).TryRestoreAsync(Key, null, Token);
        await Assert.That(File.ReadAllText(file)).IsEqualTo("first");
    }

    [Test]
    public async Task SecondWriterWithAnotherBuildTimeKeepsTheFirst()
    {
        var file = InRepo(repo, "bin/a", "a");
        var cache = new OutputCache(store, repo);
        await Assert.That(await cache.SaveAsync(Key, [file], [], [], TimeSpan.FromSeconds(1), Token)).IsTrue();
        await Assert.That(await cache.SaveAsync(Key, [file], [], [], TimeSpan.FromSeconds(2), Token)).IsTrue();

        await Assert.That((await cache.TryRestoreAsync(Key, null, Token))?.BuildTime).IsEqualTo(TimeSpan.FromSeconds(1));
    }

    [Test]
    public async Task TieredRestoreFetchesFromRemoteIntoLocal()
    {
        var remote = new FileSystemStore(Path.Combine(temp, "remote"));
        await new OutputCache(remote, repo).SaveAsync(Key, [InRepo(repo, "bin/a", "a")], [], [], TimeSpan.Zero, Token);

        var other = Path.Combine(temp, "other");
        await Assert.That(await new OutputCache(new TieredStore(store, remote), other).TryRestoreAsync(Key, null, Token)).IsNotNull();

        await Assert.That(File.ReadAllText(Path.Combine(other, "bin", "a"))).IsEqualTo("a");
        Directory.Delete(Path.Combine(temp, "remote"), recursive: true);
        await Assert.That(await new OutputCache(store, Path.Combine(temp, "third")).TryRestoreAsync(Key, null, Token)).IsNotNull();
    }

    [Test]
    public async Task TieredWriterSeesTheManifestRemoteKept()
    {
        var remote = new FileSystemStore(Path.Combine(temp, "remote"));
        var file = InRepo(repo, "bin/a", "first");
        await new OutputCache(remote, repo).SaveAsync(Key, [file], [], [], TimeSpan.Zero, Token);

        File.WriteAllText(file, "second");
        var tiered = new OutputCache(new TieredStore(store, remote), repo);
        await Assert.That(await tiered.SaveAsync(Key, [file], [], [], TimeSpan.Zero, Token)).IsFalse();

        await tiered.TryRestoreAsync(Key, null, Token);
        await Assert.That(File.ReadAllText(file)).IsEqualTo("first");
    }

    private sealed class CountingRemote(Func<int, Task<byte[]?>> get) : IObjectStore
    {
        private int gets;

        public int Gets => gets;

        public Task<bool> TryCreateAsync(string key, ReadOnlyMemory<byte> content, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken) => get(Interlocked.Increment(ref gets));

        public Task<bool> TryGetFileAsync(string key, string destinationFile, CancellationToken cancellationToken) => Task.FromResult(false);
    }

    [Test]
    public async Task TieredMissesOfOneKeyDownloadItOnce()
    {
        var download = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var remote = new CountingRemote(_ => download.Task);
        var tiered = new TieredStore(store, remote);

        var gets = Enumerable.Range(0, 8).Select(_ => tiered.GetAsync("k", Token)).ToList();
        download.SetResult([1]);
        await Task.WhenAll(gets);

        await Assert.That(remote.Gets).IsEqualTo(1);
    }

    [Test]
    public async Task TieredFailedDownloadIsNotReused()
    {
        var tiered = new TieredStore(store, new CountingRemote(n =>
            n == 1 ? Task.FromException<byte[]?>(new HttpRequestException("first")) : Task.FromResult<byte[]?>([1])));

        await Assert.ThrowsAsync<HttpRequestException>(() => tiered.GetAsync("k", Token));

        await Assert.That(await tiered.GetAsync("k", Token)).IsNotNull();
    }

    [Test]
    public async Task TieredCallerCancellingLeavesTheDownloadToOthers()
    {
        var download = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var remote = new CountingRemote(_ => download.Task);
        var tiered = new TieredStore(store, remote);
        using var cancel = new CancellationTokenSource();

        var cancelled = tiered.GetAsync("k", cancel.Token);
        var other = tiered.GetAsync("k", Token);
        await cancel.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => cancelled);

        download.SetResult([1]);
        await Assert.That(await other).IsNotNull();
        await Assert.That(remote.Gets).IsEqualTo(1);
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

        await Assert.ThrowsExactlyAsync<TException>(() => new OutputCache(store, repo).TryRestoreAsync(Key, null, Token));

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
