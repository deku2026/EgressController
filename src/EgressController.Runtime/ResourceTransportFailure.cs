namespace EgressController.Runtime;

public enum ResourceTransportStage { Bind, Dns, Tcp, Tls, Http, Body }

/// <summary>Retains actionable transport facts without retaining exception messages, URLs or response bodies.</summary>
public sealed class ResourceTransportFailure(ResourceTransportStage stage, string code, int? httpStatus = null)
    : IOException($"Resource transport failed: stage={stage}; code={code}; http={httpStatus}")
{
    public ResourceTransportStage TransportStage { get; } = stage;
    public string Code { get; } = code;
    public int? HttpStatus { get; } = httpStatus;
    public string UserReason => HttpStatus is { } status ? $"服务器返回 HTTP {status}"
        : Code switch
        {
            "response.encoding" => "服务器使用了不支持的响应压缩",
            "response.size" => "订阅响应超过大小限制",
            "redirect.invalid" => "订阅重定向无效或次数过多",
            _ => $"{StageLabel}：{NetworkFailure.Label(Code)}"
        };
    private string StageLabel => TransportStage switch
    { ResourceTransportStage.Bind => "网卡绑定", ResourceTransportStage.Dns => "DNS 查询", ResourceTransportStage.Tcp => "建立连接", ResourceTransportStage.Tls => "TLS 验证", ResourceTransportStage.Http => "HTTPS 请求", _ => "读取响应" };
}
