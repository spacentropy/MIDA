using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using Testcontainers.FakeGcsServer;
using TUnit.Core.Interfaces;

namespace Mida.Tests;

/// <summary>fake-gcs-server through Testcontainers for the whole run. Without Linux Docker, <see cref="Endpoint"/> is null and its tests skip.</summary>
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
        if (!await LinuxDocker())
        {
            return;
        }

        container = new FakeGcsServerBuilder("fsouza/fake-gcs-server:1.56.1").Build();
        await container.StartAsync();

        // The connection string ends in storage/v1/, and the emulator variable takes the server's root.
        var endpoint = new Uri(new Uri(container.GetConnectionString()), "/");
        using var body = new StringContent($$"""{"name":"{{Bucket}}"}""");
        using var created = await Http.PostAsync(new Uri(endpoint, "storage/v1/b"), body);
        created.EnsureSuccessStatusCode();

        Endpoint = endpoint;
        // mida build runs as a child process, which inherits it.
        Environment.SetEnvironmentVariable("STORAGE_EMULATOR_HOST", endpoint.ToString());
    }

    /// <summary>
    /// The image is Linux only. Windows runners have Docker with Windows containers, and macOS runners have none, but a
    /// laptop can run Linux containers on either. Testcontainers keeps no endpoint when no daemon answers.
    /// </summary>
    private static async Task<bool> LinuxDocker()
    {
        if (TestcontainersSettings.OS.DockerEndpointAuthConfig is not { } endpoint)
        {
            return false;
        }

        using var client = endpoint.GetDockerClientBuilder(ResourceReaper.DefaultSessionId).Build();
        return (await client.System.GetSystemInfoAsync()).OSType == "linux";
    }

    public async ValueTask DisposeAsync()
    {
        if (container is not null)
        {
            await container.DisposeAsync();
        }
    }
}
