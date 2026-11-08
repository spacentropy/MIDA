using System.Diagnostics;
using TUnit.Core.Interfaces;

namespace Mida.Tests;

/// <summary>fake-gcs-server in Docker for the whole run. Without Docker, <see cref="Endpoint"/> is null and its tests skip.</summary>
public sealed class GcsEmulator : IAsyncInitializer, IAsyncDisposable
{
    public const string Bucket = "mida-test";

    private static readonly HttpClient Http = new();

    private string? container;

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
        if (Docker("run", "-d", "--rm", "-p", "127.0.0.1::4443", "fsouza/fake-gcs-server:1.56.1", "-scheme", "http") is not { } id)
        {
            return;
        }

        container = id;
        var endpoint = new Uri($"http://{Docker("port", id, "4443")!.Split('\n')[0]}/");
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var body = new StringContent($$"""{"name":"{{Bucket}}"}""");
                using var created = await Http.PostAsync(new Uri(endpoint, "storage/v1/b"), body);
                created.EnsureSuccessStatusCode();
                break;
            }
            catch (HttpRequestException) when (attempt < 50)
            {
                await Task.Delay(200);
            }
        }

        Endpoint = endpoint;
        // mida build runs as a child process, which inherits it.
        Environment.SetEnvironmentVariable("STORAGE_EMULATOR_HOST", endpoint.ToString());
    }

    public ValueTask DisposeAsync()
    {
        if (container is not null)
        {
            Docker("rm", "-f", container);
        }

        return ValueTask.CompletedTask;
    }

    private static string? Docker(params string[] arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("docker", arguments) { RedirectStandardOutput = true, RedirectStandardError = true })!;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0 ? output.Trim() : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}
