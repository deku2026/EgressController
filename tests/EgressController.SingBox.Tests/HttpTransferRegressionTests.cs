using System.Net;
using EgressController.SingBox.Core;
using EgressController.Transport.Upstream;

namespace EgressController.SingBox.Tests;

public sealed class HttpTransferRegressionTests
{
    [Theory]
    [InlineData("rules")]
    [InlineData("metadata")]
    [InlineData("core")]
    public async Task Timeout_covers_body_after_successful_headers(string kind)
    {
        using var stream = new StalledBody();
        using var client = new HttpClient(new Handler(stream)) { Timeout = TimeSpan.FromMilliseconds(100) };
        using var destination = new MemoryStream();
        using var fetcher = new HttpRemoteFetcher(client);
        var release = new SingBoxReleaseClient(client);
        Task transfer = kind switch
        {
            "rules" => fetcher.FetchAsync(new("https://mock.invalid/rules"), 1024, TestContext.Current.CancellationToken).AsTask(),
            "metadata" => release.GetLatestStableAsync(TestContext.Current.CancellationToken),
            _ => release.DownloadAsync(Asset(1024), destination, TestContext.Current.CancellationToken)
        };
        var failure = await Assert.ThrowsAsync<DownloadException>(() => transfer.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Equal("download.timeout", failure.Code);
        Assert.True(stream.ReadStarted);
    }

    [Theory]
    [InlineData(3, 2)]
    [InlineData(1, 2)]
    public async Task Asset_body_must_match_declared_size(int actual, long declared)
    {
        using var client = new HttpClient(new Handler(new MemoryStream(new byte[actual])));
        using var output = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() => new SingBoxReleaseClient(client)
            .DownloadAsync(Asset(declared), output, TestContext.Current.CancellationToken));
        Assert.True(output.Length <= declared);
    }

    [Fact]
    public async Task Chunked_rule_body_cannot_exceed_limit_without_content_length()
    {
        using var client = new HttpClient(new Handler(new MemoryStream(new byte[8193])));
        using var fetcher = new HttpRemoteFetcher(client);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            fetcher.FetchAsync(new("https://mock.invalid/rules"), 8192, TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task Finite_body_is_downloaded_completely_without_network()
    {
        byte[] bytes = [1, 2, 3];
        using var client = new HttpClient(new Handler(new MemoryStream(bytes)));
        using var output = new MemoryStream();
        await new SingBoxReleaseClient(client).DownloadAsync(Asset(3), output, TestContext.Current.CancellationToken);
        Assert.Equal(bytes, output.ToArray());
    }

    private static SingBoxReleaseAsset Asset(long size) => new() { Size = size, BrowserDownloadUrl = "https://mock.invalid/core.zip" };
    private sealed class Handler(Stream body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) });
    }
    private sealed class StalledBody : Stream
    {
        public bool ReadStarted;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadStarted = true;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
    }
}
