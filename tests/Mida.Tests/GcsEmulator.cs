using DotNet.Testcontainers.Builders;
using Testcontainers.FakeGcsServer;
using TUnit.Core.Interfaces;

namespace Mida.Tests;

/// <summary>fake-gcs-server through Testcontainers for the whole run. Without Docker, <see cref="Endpoint"/> is null and its tests skip.</summary>
public sealed class GcsEmulator : IAsyncInitializer, IAsyncDisposable
{
    public const string Bucket = "mida-test";

    private static readonly HttpClient Http = new();

    private FakeGcsServerContainer? container;

    public Uri? Endpoint { get; private set; }

    /// <summary>A fresh prefix, so tests share the bucket without seeing each other's objects.</summary>
    public string Prefix()
    {
        if (Endpoint is null)
        {
            Skip.Test("Docker is not available");
        }

        return $"{Guid.NewGuid():N}/";
    }

    public async Task InitializeAsync()
    {
        try
        {
            var started = new FakeGcsServerBuilder("fsouza/fake-gcs-server:1.56.1").Build();
            await started.StartAsync();
            container = started;
        }
        catch (DockerUnavailableException)
        {
            return;
        }

        // The connection string ends in storage/v1/, and the emulator variable takes the server's root.
        var endpoint = new Uri(new Uri(container.GetConnectionString()), "/");
        using var body = new StringContent($$"""{"name":"{{Bucket}}"}""");
        using var created = await Http.PostAsync(new Uri(endpoint, "storage/v1/b"), body);
        created.EnsureSuccessStatusCode();

        Endpoint = endpoint;
        // mida build runs as a child process, which inherits it.
        Environment.SetEnvironmentVariable("STORAGE_EMULATOR_HOST", endpoint.ToString());
    }

    public async ValueTask DisposeAsync()
    {
        if (container is not null)
        {
            await container.DisposeAsync();
        }
    }
}
