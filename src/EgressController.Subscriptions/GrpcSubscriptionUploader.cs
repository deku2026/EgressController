using System.Net;
using System.Security.Cryptography;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using SubVault.Ingress.V1;

namespace EgressController.Subscriptions;

public sealed class GrpcSubscriptionUploader : ISubscriptionUploader, IDisposable
{
    private readonly GrpcChannel _channel;
    private readonly string _token;
    public GrpcSubscriptionUploader(Uri endpoint, string token, HttpMessageHandler? handler = null)
    {
        if (endpoint.Scheme is not ("https" or "http") || endpoint.UserInfo.Length > 0 || endpoint.Query.Length > 0 || endpoint.Fragment.Length > 0)
            throw new ArgumentException("服务器地址无效。");
        if (endpoint.Scheme == "http" && !(IPAddress.TryParse(endpoint.Host, out var ip) && IPAddress.IsLoopback(ip)))
            throw new ArgumentException("远程上传必须使用 HTTPS；明文 HTTP 仅供本机隔离测试。");
        if (token.Length < 32) throw new ArgumentException("接收凭据至少需要 32 个字符。");
        _token = token;
        _channel = GrpcChannel.ForAddress(endpoint, new GrpcChannelOptions { HttpHandler = handler ?? new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false }, MaxSendMessageSize = 4 * 1024 * 1024 + 4096 });
    }
    public async Task<SubscriptionResult> UploadAsync(string provider, string client, SourceFormat format, byte[] content, long version, DateTimeOffset captured, CancellationToken token)
    {
        string hash = Convert.ToHexStringLower(SHA256.HashData(content));
        var request = new SubscriptionUpload { ProviderId = provider, ClientId = client, Format = (SubscriptionFormat)format, Content = ByteString.CopyFrom(content), Sha256 = hash, Version = version, CapturedAtUnixMs = captured.ToUnixTimeMilliseconds() };
        var rpc = new SubscriptionIngress.SubscriptionIngressClient(_channel);
        UploadReceipt receipt;
        try { receipt = await rpc.UploadSubscriptionAsync(request, new Metadata { { "authorization", "Bearer " + _token } }, deadline: DateTime.UtcNow.AddSeconds(30), cancellationToken: token).ConfigureAwait(false); }
        catch (RpcException ex) when (!token.IsCancellationRequested) { throw new IOException($"服务器接收失败（{ex.StatusCode}）；有效订阅已保留，可重试上传。"); }
        if (!receipt.Accepted || receipt.Format != request.Format || receipt.Sha256 != hash || receipt.Version != version || string.IsNullOrWhiteSpace(receipt.ServerVersion))
            throw new InvalidDataException("服务器未确认相同格式、内容哈希和版本；不能显示上传成功。");
        return new(format, hash, receipt.ServerVersion, version);
    }
    public void Dispose() => _channel.Dispose();
}
