using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using Google.Apis.Auth.OAuth2;
using Testcontainers.FakeGcsServer;
using TUnit.Core.Interfaces;

namespace Mida.Tests;

/// <summary>
/// The bucket for the whole run: real GCS when MIDA_TEST_BUCKET names one, else fake-gcs-server through Testcontainers.
/// With neither, <see cref="Endpoint"/> is null and its tests skip.
/// </summary>
public sealed class GcsEmulator : IAsyncInitializer, IAsyncDisposable
{
    private static readonly HttpClient Http = new();

    private FakeGcsServerContainer? container;

    public Uri? Endpoint { get; private set; }

    public string Bucket { get; private set; } = "mida-test";

    /// <summary>An OAuth access token, or null for the emulator.</summary>
    public Func<CancellationToken, Task<string?>> Token { get; private set; } = _ => Task.FromResult<string?>(null);

    /// <summary>A fresh prefix, so tests share the bucket without seeing each other's objects.</summary>
    public string Prefix()
    {
        if (Endpoint is null)
        {
            Skip.Test("Neither MIDA_TEST_BUCKET nor Docker is available");
        }

        return $"{Guid.NewGuid():N}/";
    }

    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("MIDA_TEST_BUCKET") is { Length: > 0 } bucket)
        {
            // Application Default Credentials, which mida build also finds: the metadata server on Cloud Build.
            var credential = (await GoogleCredential.GetApplicationDefaultAsync()).CreateScoped("https://www.googleapis.com/auth/devstorage.read_write");
            Bucket = bucket;
            Token = async token => await ((ITokenAccess)credential).GetAccessTokenForRequestAsync(cancellationToken: token);
            Endpoint = new Uri("https://storage.googleapis.com/");
            return;
        }

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
