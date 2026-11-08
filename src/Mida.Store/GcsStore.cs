using System.Net;
using System.Net.Http.Headers;

namespace Mida.Store;

/// <summary>Google Cloud Storage through its JSON API. Create-only through <c>ifGenerationMatch=0</c>, where 412 means it exists.</summary>
/// <param name="token">An OAuth access token, or null for an emulator.</param>
public sealed class GcsStore(HttpClient http, Uri endpoint, string bucket, string prefix, Func<CancellationToken, Task<string?>> token) : IObjectStore
{
    // shortcut: no retry, so a 5xx or a timeout is a miss with a warning. Retry 429, 5xx and timeouts if CI runs show them.
    public async Task<bool> TryCreateAsync(string key, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        // Asked first: most blobs exist already, and a duplicate upload sends the whole body before its 412.
        using (var existing = await SendAsync(HttpMethod.Get, $"storage/v1/b/{bucket}/o/{Name(key)}?fields=name", null, cancellationToken))
        {
            if (existing.StatusCode == HttpStatusCode.OK)
            {
                return false;
            }

            if (existing.StatusCode != HttpStatusCode.NotFound)
            {
                existing.EnsureSuccessStatusCode();
            }
        }

        using var response = await SendAsync(
            HttpMethod.Post,
            $"upload/storage/v1/b/{bucket}/o?uploadType=media&ifGenerationMatch=0&name={Name(key)}",
            new ReadOnlyMemoryContent(content),
            cancellationToken);
        if (response.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            return false;
        }

        response.EnsureSuccessStatusCode();
        return true;
    }

    public async Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, $"storage/v1/b/{bucket}/o/{Name(key)}?alt=media", null, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    public async Task<bool> TryGetFileAsync(string key, string destinationFile, CancellationToken cancellationToken)
    {
        if (await GetAsync(key, cancellationToken) is not { } content)
        {
            return false;
        }

        await File.WriteAllBytesAsync(destinationFile, content, cancellationToken);
        return true;
    }

    private string Name(string key) => Uri.EscapeDataString(prefix + key);

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, new Uri(endpoint, path)) { Content = content };
        if (await token(cancellationToken) is { } accessToken)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        return await http.SendAsync(request, cancellationToken);
    }
}
