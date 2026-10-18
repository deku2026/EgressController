using System.Net;
using EgressController.SingBox.Core;
using EgressController.Transport.Upstream;

namespace EgressController.SingBox.Tests;

public sealed class DefaultProxyDownloadTests
{
    [Fact]
    public async Task Catalog_rules_release_metadata_and_zip_share_the_selected_default_port()
    {
        int port = 7890;
        var seen = new List<(int Port, string Path)>();
        var transport = new DefaultProxyDownloadTransport(() => port, value => new(new Handler(request =>
        {
            seen.Add((value, request.RequestUri!.AbsolutePath));
            return request.RequestUri.AbsolutePath.EndsWith("/latest") ? Metadata() : Ok("abc");
        })));
        using var fetcher = new HttpRemoteFetcher(transport);
        var release = new SingBoxReleaseClient(transport);
        await fetcher.FetchAsync(new("https://mock.invalid/catalog"), 1024, TestContext.Current.CancellationToken);
        await fetcher.FetchAsync(new("https://mock.invalid/openai.srs"), 1024, TestContext.Current.CancellationToken);
        var version = await release.GetLatestStableAsync(TestContext.Current.CancellationToken);
        port = 7891;
        using var output = new MemoryStream();
        await release.DownloadAsync(version.Assets[0], output, TestContext.Current.CancellationToken);
        Assert.Equal([7890, 7890, 7890, 7891], seen.Select(item => item.Port));
        Assert.Equal("abc"u8.ToArray(), output.ToArray());
    }

    [Fact]
    public async Task Preparing_an_uncommitted_profile_uses_its_new_port_and_restores_the_current_profile_context()
    {
        var ports = new List<int>();
        var transport = new DefaultProxyDownloadTransport(() => 7890, port => { ports.Add(port); return new(new Handler(_ => Ok("a"))); });
        using var fetcher = new HttpRemoteFetcher(transport);
        using (transport.UseDefaultPort(7891))
        {
            await Task.Yield();
            await fetcher.FetchAsync(new("https://mock.invalid/rule"), 32, TestContext.Current.CancellationToken);
        }
        await fetcher.FetchAsync(new("https://mock.invalid/catalog"), 32, TestContext.Current.CancellationToken);
        Assert.Equal([7891, 7890], ports);
        using (transport.UseDefaultPort(null))
            await Assert.ThrowsAsync<DownloadException>(() => fetcher.FetchAsync(new("https://mock.invalid/blocked"), 32, TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(2, ports.Count);
    }

    [Fact]
    public async Task Timeout_is_descriptive_and_retry_can_recover_without_changing_port_or_wifi()
    {
        int attempts = 0;
        var transport = new DefaultProxyDownloadTransport(() => 7890, _ => new(new Handler(_ =>
            ++attempts == 1 ? throw new TaskCanceledException("connect timeout", new TimeoutException("ConnectTimeout")) : Ok("ok"))));
        using var fetcher = new HttpRemoteFetcher(transport);
        var error = await Assert.ThrowsAsync<DownloadException>(() => fetcher.FetchAsync(new("https://mock.invalid/openai.srs"), 32, TestContext.Current.CancellationToken).AsTask());
        Assert.Equal("download.timeout", error.Code);
        Assert.Contains("127.0.0.1:7890", error.Message);
        Assert.Contains("openai.srs", error.Message);
        Assert.True((await fetcher.FetchAsync(new("https://mock.invalid/openai.srs"), 32, TestContext.Current.CancellationToken)).Succeeded);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task Missing_or_failed_proxy_never_selects_a_direct_client()
    {
        int creations = 0;
        var missing = new DefaultProxyDownloadTransport(() => null, _ => { creations++; throw new Exception(); });
        var error = await Assert.ThrowsAsync<DownloadException>(() => missing.ExecuteAsync(new("https://mock.invalid/"), (_, _) => Task.FromResult(true), TestContext.Current.CancellationToken));
        Assert.Equal("download.proxy.missing", error.Code); Assert.Equal(0, creations);
        var failed = new DefaultProxyDownloadTransport(() => 7890, _ => { creations++; return new(new Handler(_ => throw new HttpRequestException("refused"))); });
        await Assert.ThrowsAsync<DownloadException>(() => failed.ExecuteAsync(new("https://mock.invalid/"), (client, ct) => client.GetStringAsync("https://mock.invalid/", ct), TestContext.Current.CancellationToken));
        Assert.Equal(1, creations);
    }

    [Fact]
    public async Task Caller_cancellation_stays_cancellation_and_diagnostics_cannot_break_downloads()
    {
        var transport = new DefaultProxyDownloadTransport(() => 7890, _ => new(new Handler(_ => Ok("ok"))));
        transport.Activity += _ => throw new IOException("logging failed");
        using var fetcher = new HttpRemoteFetcher(transport);
        Assert.True((await fetcher.FetchAsync(new("https://mock.invalid/"), 32, TestContext.Current.CancellationToken)).Succeeded);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetcher.FetchAsync(new("https://mock.invalid/"), 32, cancellation.Token).AsTask());
    }

    [Fact]
    public async Task Concurrent_catalog_update_does_not_inherit_another_preparation_port()
    {
        var ports = new System.Collections.Concurrent.ConcurrentBag<int>();
        var transport = new DefaultProxyDownloadTransport(() => 7890, port => { ports.Add(port); return new(new Handler(_ => Ok("ok"))); });
        using var fetcher = new HttpRemoteFetcher(transport);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var preparation = Task.Run(async () =>
        {
            using var scope = transport.UseDefaultPort(7891);
            entered.SetResult(); await release.Task.WaitAsync(TestContext.Current.CancellationToken);
            await fetcher.FetchAsync(new("https://mock.invalid/rule"), 32, TestContext.Current.CancellationToken);
        }, TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        await fetcher.FetchAsync(new("https://mock.invalid/catalog"), 32, TestContext.Current.CancellationToken);
        release.SetResult(); await preparation;
        Assert.Equal([7890, 7891], ports.Order());
    }

    [Fact]
    public async Task Application_shutdown_cancels_manual_downloads_with_no_explicit_caller_token()
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var transport = new DefaultProxyDownloadTransport(() => 7890, _ => new(new Handler(_ =>
        {
            lifetime.Cancel(); throw new OperationCanceledException(lifetime.Token);
        })), lifetime.Token);
        using var fetcher = new HttpRemoteFetcher(transport);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetcher.FetchAsync(new("https://mock.invalid/catalog"), 32, TestContext.Current.CancellationToken).AsTask());
    }

    private static HttpResponseMessage Ok(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text) };
    private static HttpResponseMessage Metadata() => Ok("""{"tag_name":"v1.13.19","assets":[{"name":"sing-box-1.13.19-windows-amd64.zip","size":3,"browser_download_url":"https://mock.invalid/core.zip"}]}""");
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(send(request));
    }
}
