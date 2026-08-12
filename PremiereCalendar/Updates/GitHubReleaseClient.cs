using System.Net.Http.Headers;
using System.Text.Json;

namespace PremiereCalendar.Updates;

public sealed class GitHubReleaseClient(HttpClient httpClient)
{
    private const long MaxAssetBytes = 1L * 1024 * 1024 * 1024;
    private const long MaxManifestBytes = 1L * 1024 * 1024;

    public async Task<(string ManifestPath, string PackagePath)> DownloadStableAsync(
        string owner, string repository, string destination, CancellationToken cancellationToken = default)
    {
        ValidateSegment(owner, nameof(owner));
        ValidateSegment(repository, nameof(repository));
        var root = Path.GetFullPath(destination);
        Directory.CreateDirectory(root);
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://api.github.com/repos/{owner}/{repository}/releases/latest");
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("PremiereCalendar", "1.0"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is { } metadataLength && metadataLength > MaxManifestBytes)
            throw new InvalidDataException("GitHub release metadata is too large.");
        await using var metadata = await ReadBoundedAsync(response.Content, MaxManifestBytes, cancellationToken);
        using var document = await JsonDocument.ParseAsync(metadata, cancellationToken: cancellationToken);
        var assets = document.RootElement.GetProperty("assets").EnumerateArray()
            .Select(a => (Name: a.GetProperty("name").GetString()!, Url: a.GetProperty("browser_download_url").GetString()!))
            .ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);
        if (!assets.TryGetValue("stable.manifest.json", out var manifestAsset))
            throw new InvalidDataException("GitHub release has no stable manifest.");
        var manifestPath = Path.Combine(root, manifestAsset.Name);
        await DownloadAssetAsync(manifestAsset.Url, manifestPath, MaxManifestBytes, cancellationToken);
        var manifest = JsonSerializer.Deserialize<ReleaseManifest>(await File.ReadAllTextAsync(manifestPath, cancellationToken))
            ?? throw new InvalidDataException("Stable manifest is invalid.");
        if (Path.GetFileName(manifest.PackageFileName) != manifest.PackageFileName
            || !manifest.PackageFileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Manifest package filename is unsafe.");
        if (!assets.TryGetValue(manifest.PackageFileName, out var packageAsset))
            throw new InvalidDataException("Manifest package asset is missing from the GitHub release.");
        var packagePath = Path.Combine(root, manifest.PackageFileName);
        await DownloadAssetAsync(packageAsset.Url, packagePath, MaxAssetBytes, cancellationToken);
        return (manifestPath, packagePath);
    }

    private async Task DownloadAssetAsync(string url, string path, long maxBytes, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Release asset URL must be HTTPS on github.com.");
        if (File.Exists(path)) throw new IOException($"Refusing to overwrite existing release asset: {path}");
        using var response = await httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is { } contentLength && contentLength > maxBytes)
            throw new InvalidDataException("Release asset is too large.");
        try
        {
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);
            var buffer = new byte[64 * 1024];
            long total = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
            {
                total += read;
                if (total > maxBytes) throw new InvalidDataException("Release asset is too large.");
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        }
        catch
        {
            File.Delete(path);
            throw;
        }
    }

    private static async Task<MemoryStream> ReadBoundedAsync(HttpContent content, long maxBytes, CancellationToken cancellationToken)
    {
        await using var input = await content.ReadAsStreamAsync(cancellationToken);
        var output = new MemoryStream();
        var buffer = new byte[32 * 1024];
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (output.Length + read > maxBytes)
            {
                output.Dispose();
                throw new InvalidDataException("GitHub release metadata is too large.");
            }
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        output.Position = 0;
        return output;
    }

    private static void ValidateSegment(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(c => !(char.IsLetterOrDigit(c) || c is '-' or '_' or '.')))
            throw new ArgumentException("GitHub owner/repository segment is invalid.", name);
    }
}
