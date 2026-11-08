using System.Text;
using Mida.Store;

namespace Mida.Tests;

public sealed class GcsStoreTests : IDisposable
{
    private static readonly HttpClient Http = new();

    private readonly string temp = Directory.CreateTempSubdirectory("mida-tests-").FullName;

    [ClassDataSource<GcsEmulator>(Shared = SharedType.PerTestSession)]
    public required GcsEmulator Emulator { get; init; }

    private static CancellationToken Token => TestContext.Current!.Execution.CancellationToken;

    public void Dispose() => Directory.Delete(temp, recursive: true);

    private GcsStore Store(string prefix) =>
        new(Http, Emulator.Endpoint!, GcsEmulator.Bucket, prefix, _ => Task.FromResult<string?>(null));

    [Test]
    public async Task CreateOnlyKeepsTheFirstWriter()
    {
        var store = Store(Emulator.Prefix());

        await Assert.That(await store.TryCreateAsync("v1/ac/k", "first"u8.ToArray(), Token)).IsTrue();
        await Assert.That(await store.TryCreateAsync("v1/ac/k", "second"u8.ToArray(), Token)).IsFalse();

        await Assert.That(Encoding.UTF8.GetString((await store.GetAsync("v1/ac/k", Token))!)).IsEqualTo("first");
    }

    [Test]
    public async Task MissingKeyIsAMiss()
    {
        var store = Store(Emulator.Prefix());
        var destination = Path.Combine(temp, "a");

        await Assert.That(await store.GetAsync("v1/ac/missing", Token)).IsNull();
        await Assert.That(await store.TryGetFileAsync("v1/ac/missing", destination, Token)).IsFalse();
        await Assert.That(File.Exists(destination)).IsFalse();
    }

    [Test]
    public async Task RestoresAFileOnlyUnderItsPrefix()
    {
        var store = Store(Emulator.Prefix());
        await store.TryCreateAsync("v1/cas/ab/abc", "blob"u8.ToArray(), Token);
        var destination = Path.Combine(temp, "a");

        await Assert.That(await store.TryGetFileAsync("v1/cas/ab/abc", destination, Token)).IsTrue();
        await Assert.That(File.ReadAllText(destination)).IsEqualTo("blob");
        await Assert.That(await Store(Emulator.Prefix()).GetAsync("v1/cas/ab/abc", Token)).IsNull();
    }
}
