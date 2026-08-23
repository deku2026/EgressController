using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

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
    private readonly HttpClient _client;

    public SingBoxReleaseClient(HttpClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        if (!_client.DefaultRequestHeaders.UserAgent.Any())
            _client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("EgressController", "1.0"));
    }

    public async Task<SingBoxRelease> GetLatestStableAsync(CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await _client.GetAsync(
            LatestReleaseEndpoint,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        SingBoxRelease release = await JsonSerializer.DeserializeAsync(
                stream,
                SingBoxJsonContext.Default.SingBoxRelease,
                cancellationToken)
            .ConfigureAwait(false)
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

        using HttpResponseMessage response = await _client.GetAsync(
            uri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await source.CopyToAsync(destination, 128 * 1024, cancellationToken).ConfigureAwait(false);
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
