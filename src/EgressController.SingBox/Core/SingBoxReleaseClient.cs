using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using EgressController.Transport.Upstream;

namespace EgressController.SingBox.Core;

public sealed record SingBoxReleaseAsset
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("browser_download_url")]
    public string BrowserDownloadUrl { get; init; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; init; }

    [JsonPropertyName("digest")]
    public string? Digest { get; init; }
}

public sealed record SingBoxRelease
{
    [JsonPropertyName("tag_name")]
    public string TagName { get; init; } = string.Empty;

    [JsonPropertyName("draft")]
    public bool Draft { get; init; }

    [JsonPropertyName("prerelease")]
    public bool Prerelease { get; init; }

    [JsonPropertyName("published_at")]
    public DateTimeOffset? PublishedAtUtc { get; init; }

    [JsonPropertyName("assets")]
    public SingBoxReleaseAsset[] Assets { get; init; } = Array.Empty<SingBoxReleaseAsset>();
}

public interface ISingBoxReleaseClient
{
    Task<SingBoxRelease> GetLatestStableAsync(CancellationToken cancellationToken = default);
    Task DownloadAsync(SingBoxReleaseAsset asset, Stream destination, CancellationToken cancellationToken = default);
}

public sealed class SingBoxReleaseClient : ISingBoxReleaseClient
{
    public const string LatestReleaseEndpoint = "https://api.github.com/repos/SagerNet/sing-box/releases/latest";
    private readonly IHttpDownloadTransport _transport;

    public SingBoxReleaseClient(HttpClient client) : this(new ClientDownloadTransport(client)) { }
    public SingBoxReleaseClient(IHttpDownloadTransport transport) => _transport = transport;

    public async Task<SingBoxRelease> GetLatestStableAsync(CancellationToken cancellationToken = default)
    {
        return await _transport.ExecuteAsync(new(LatestReleaseEndpoint), async (client, token) =>
        {
            using HttpResponseMessage response = await client.GetAsync(
                LatestReleaseEndpoint,
                HttpCompletionOption.ResponseHeadersRead,
                token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using Stream stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var payload = new MemoryStream();
            await HttpTransfer.CopyBoundedAsync(stream, payload, 4 * 1024 * 1024, token).ConfigureAwait(false);
            SingBoxRelease release = JsonSerializer.Deserialize(payload.ToArray(), SingBoxJsonContext.Default.SingBoxRelease)
                ?? throw new SingBoxReleaseException("GitHub release JSON 为空。");

            if (release.Draft || release.Prerelease)
                throw new SingBoxReleaseException("GitHub latest release 不是 stable release。");
            if (!TryParseSupportedVersion(release.TagName, out Version? version))
                throw new SingBoxReleaseException($"不支持的 sing-box release tag：{release.TagName}");

            string expected = $"sing-box-{version}-windows-amd64.zip";
            SingBoxReleaseAsset? asset = release.Assets.FirstOrDefault(
                candidate => string.Equals(candidate.Name, expected, StringComparison.Ordinal));
            if (asset is null || !Uri.TryCreate(asset.BrowserDownloadUrl, UriKind.Absolute, out Uri? uri)
                || uri.Scheme is not ("http" or "https"))
            {
                throw new SingBoxReleaseException($"release {release.TagName} 缺少 windows-amd64 ZIP。");
            }

            return release;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task DownloadAsync(
        SingBoxReleaseAsset asset,
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(destination);
        if (!Uri.TryCreate(asset.BrowserDownloadUrl, UriKind.Absolute, out Uri? uri)
            || uri.Scheme is not ("http" or "https"))
            throw new SingBoxReleaseException("release asset URL 不是受支持的 HTTP(S) 地址。");
        if (asset.Size is <= 0 or > 512L * 1024 * 1024)
            throw new SingBoxReleaseException("release asset 大小不在允许范围内。");
        await _transport.ExecuteAsync(uri, async (client, token) =>
        {

            using HttpResponseMessage response = await client.GetAsync(
                uri,
                HttpCompletionOption.ResponseHeadersRead,
                token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using Stream source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            long copied = await HttpTransfer.CopyBoundedAsync(source, destination, asset.Size, token).ConfigureAwait(false);
            if (copied != asset.Size) throw new InvalidDataException("release asset 正文大小与声明不一致。");
            return true;
        }, cancellationToken, TimeSpan.FromMinutes(5)).ConfigureAwait(false);
    }

    public static bool TryParseSupportedVersion(string tag, out Version? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(tag))
            return false;
        string value = tag.Trim();
        if (value.StartsWith('v'))
            value = value[1..];
        if (!Version.TryParse(value, out Version? parsed)
            || parsed.Major != 1
            || parsed.Minor != 13
            || parsed.Build < 0)
            return false;
        version = parsed;
        return true;
    }
}

public sealed class SingBoxReleaseException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(SingBoxRelease))]
[JsonSerializable(typeof(SingBoxReleaseAsset))]
internal sealed partial class SingBoxJsonContext : JsonSerializerContext;
