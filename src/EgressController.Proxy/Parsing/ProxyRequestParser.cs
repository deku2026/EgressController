using System.Text;

namespace EgressController.Proxy.Parsing;

public enum ProxyRequestKind
{
    /// <summary>CONNECT host:port — a tunnel is established, then byte-relayed.</summary>
    Connect,
    /// <summary>Plain HTTP absolute-form request — forwarded request-by-request (V1 close semantics).</summary>
    Plain,
}

public enum ProxyRequestParseError
{
    None,
    RequestLineTooLong,
    HeadersTooLarge,
    InvalidRequestLine,
    UnsupportedMethod,
    InvalidAuthority,
    InvalidUri,
    HostHeaderMismatch,
    MalformedHeaderField,
    CrLfInjection,
    BytesTooShort, // need more bytes (caller should keep buffering)
}

/// <summary>A parsed, validated proxy request that the router can decide on.</summary>
public sealed class ParsedProxyRequest
{
    public required ProxyRequestKind Kind { get; init; }
    public required string Method { get; init; }
    /// <summary>Normalized (lowercased, trailing-dot trimmed) target host.</summary>
    public required string Host { get; init; }
    public required int Port { get; init; }
    /// <summary>Absolute-form target URI (Plain only).</summary>
    public required Uri? TargetUri { get; init; }

    /// <summary>Offset in the source buffer where the body begins (after the header section).</summary>
    public required int BodyOffset { get; init; }

    /// <summary>Forward-safe headers (hop-by-hop / proxy-credential headers removed).</summary>
    public required IReadOnlyList<KeyValuePair<string, string>> ForwardHeaders { get; init; }

    public required ProxyRequestParseError Error { get; init; }
    public string ErrorDetail { get; init; } = string.Empty;
}

/// <summary>
/// Byte-level HTTP proxy request parser (CONNECT authority-form + plain absolute-form).
/// Rigid on the limits the plan requires: bounded request line + headers, CRLF framing,
/// IPv6 literal authority, no absolute-form/Host mismatch, no proxy credentials / hop-by-hop
/// forwarding. Pure + AOT-safe (span based, no reflection).
/// </summary>
public static class ProxyRequestParser
{
    public const int MaxRequestLineBytes = 1024;
    public const int MaxHeaderBytes = 32 * 1024;

    private static readonly HashSet<string> HopByHop = new(StringComparer.OrdinalIgnoreCase)
    {
        "connection", "proxy-connection", "keep-alive", "proxy-authenticate",
        "proxy-authorization", "te", "trailer", "upgrade",
    };

    /// <summary>Parse the request head (request line + headers) from a fully-buffered byte span.</summary>
    public static ParsedProxyRequest Parse(ReadOnlySpan<byte> source)
    {
        int headerEnd = IndexHeaderTerminator(source);
        if (headerEnd < 0)
            return source.Length > MaxRequestLineBytes + MaxHeaderBytes
                ? Fail(ProxyRequestParseError.HeadersTooLarge, "headers exceed limit without terminator")
                : Fail(ProxyRequestParseError.BytesTooShort, "need more bytes for request head");

        ReadOnlySpan<byte> head = source[..headerEnd];
        if (ContainsLoneLineFeed(head))
            return Fail(ProxyRequestParseError.InvalidRequestLine, "lone LF without CR — malformed framing");

        // Split into CRLF-terminated lines.
        var lines = SplitLines(head);
        if (lines.Count == 0)
            return Fail(ProxyRequestParseError.InvalidRequestLine, "empty request head");

        string requestLine = Encoding.ASCII.GetString(lines[0]);
        if (requestLine.Length > MaxRequestLineBytes)
            return Fail(ProxyRequestParseError.RequestLineTooLong, "request line too long");

        var (method, target, version) = SplitRequestLine(requestLine);
        if (method is null || target is null || version is null)
            return Fail(ProxyRequestParseError.InvalidRequestLine, $"malformed request line: {requestLine}");

        if (!IsHttpVersion(version))
            return Fail(ProxyRequestParseError.InvalidRequestLine, $"unsupported version: {version}");

        // Parse headers.
        var headers = new List<KeyValuePair<string, string>>();
        string? hostHeader = null;
        for (int i = 1; i < lines.Count; i++)
        {
            if (lines[i].Length == 0)
                break;
            ReadOnlySpan<byte> line = lines[i];
            if (line[0] == ' ' || line[0] == '\t')
                return Fail(ProxyRequestParseError.InvalidRequestLine, "obs-fold continuation not allowed");

            int colon = line.IndexOf((byte)':');
            if (colon <= 0)
                return Fail(ProxyRequestParseError.MalformedHeaderField, "header without colon");

            string name = Encoding.ASCII.GetString(line[..colon]).Trim().ToLowerInvariant();
            string value = Encoding.ASCII.GetString(line[(colon + 1)..]).Trim();
            if (name.Contains(' ') || name.Length == 0)
                return Fail(ProxyRequestParseError.MalformedHeaderField, $"malformed header name: {name}");

            if (value.Contains('\r') || value.Contains('\n'))
                return Fail(ProxyRequestParseError.CrLfInjection, "CR/LF found in header value");
            if (name == "host")
                hostHeader = value;

            if (!HopByHop.Contains(name))
                headers.Add(new KeyValuePair<string, string>(name, value));
        }

        if (method.Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
            return ParseConnect(target, version, hostHeader, headers, headerEnd + 4);

        if (method.Equals("GET", StringComparison.OrdinalIgnoreCase)
            || method.Equals("POST", StringComparison.OrdinalIgnoreCase)
            || method.Equals("HEAD", StringComparison.OrdinalIgnoreCase)
            || method.Equals("PUT", StringComparison.OrdinalIgnoreCase)
            || method.Equals("DELETE", StringComparison.OrdinalIgnoreCase)
            || method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase)
            || method.Equals("PATCH", StringComparison.OrdinalIgnoreCase))
            return ParsePlain(method, target, version, hostHeader, headers, headerEnd + 4);

        return Fail(ProxyRequestParseError.UnsupportedMethod, $"method not supported: {method}");
    }

    private static ParsedProxyRequest ParseConnect(
        string authority, string version, string? hostHeader, List<KeyValuePair<string, string>> headers, int bodyOffset)
    {
        if (!TryParseAuthority(authority, out string host, out int port))
            return Fail(ProxyRequestParseError.InvalidAuthority, $"invalid CONNECT authority: {authority}");
        return new ParsedProxyRequest
        {
            Kind = ProxyRequestKind.Connect,
            Method = "CONNECT",
            Host = host,
            Port = port,
            TargetUri = null,
            BodyOffset = bodyOffset,
            ForwardHeaders = headers,
            Error = ProxyRequestParseError.None,
        };
    }

    private static ParsedProxyRequest ParsePlain(
        string method, string target, string version, string? hostHeader,
        List<KeyValuePair<string, string>> headers, int bodyOffset)
    {
        if (!Uri.TryCreate(target, UriKind.Absolute, out Uri? uri))
            return Fail(ProxyRequestParseError.InvalidUri, $"absolute-form target invalid: {target}");

        if (uri.Scheme != "http")
            return Fail(ProxyRequestParseError.InvalidUri, $"only http absolute-form supported, got {uri.Scheme}");

        string host = uri.Host; // IDN-lowered
        int port = uri.IsDefaultPort ? 80 : uri.Port;

        if (hostHeader is not null && !HostMatches(host, hostHeader))
            return Fail(ProxyRequestParseError.HostHeaderMismatch, $"absolute URI host '{host}' conflicts with Host header '{hostHeader}'");

        return new ParsedProxyRequest
        {
            Kind = ProxyRequestKind.Plain,
            Method = method,
            Host = host,
            Port = port,
            TargetUri = uri,
            BodyOffset = bodyOffset,
            ForwardHeaders = headers,
            Error = ProxyRequestParseError.None,
        };
    }

    // ---- helpers ----

    /// <summary>Index just after the blank line terminating the header section, or -1.</summary>
    internal static int IndexHeaderTerminator(ReadOnlySpan<byte> b)
        => b.IndexOf((ReadOnlySpan<byte>)"\r\n\r\n"u8) is var i && i >= 0 ? i : -1;

    private static bool HostMatches(string uriHost, string hostHeader)
    {
        // Host header may carry :port; strip it, then compare host portion case-insensitively.
        int colon = hostHeader.LastIndexOf(':');
        string hh = colon > 0 && hostHeader.IndexOf(']') < colon // not the IPv6-literal ]: separator
            ? hostHeader[..colon]
            : hostHeader;
        return string.Equals(uriHost.TrimEnd('.'), hh.TrimStart('[').TrimEnd(']').TrimEnd('.'), StringComparison.OrdinalIgnoreCase);
    }

    private static (string? Method, string? Target, string? Version) SplitRequestLine(string line)
    {
        string[] parts = line.Split(' ');
        if (parts.Length != 3 || parts[0].Length == 0 || parts[1].Length == 0 || parts[2].Length == 0)
            return (null, null, null);
        return (parts[0], parts[1], parts[2]);
    }

    private static bool IsHttpVersion(string v)
        => v.StartsWith("HTTP/1.", StringComparison.OrdinalIgnoreCase) || v.StartsWith("HTTP/2 ", StringComparison.OrdinalIgnoreCase);

    /// <summary>Parse host[:port], handling IPv6 literals in brackets.</summary>
    internal static bool TryParseAuthority(string authority, out string host, out int port)
    {
        host = "";
        port = 0;
        if (authority.Length == 0)
            return false;

        // Reject any CR/LF/control injection outright.
        foreach (char c in authority)
            if (c < 0x21 || c > 0x7e)
                return false;

        if (authority[0] == '[')
        {
            // [::1]:8443  (a bare [::1] defaults to 443)
            int close = authority.IndexOf(']');
            if (close < 0)
                return false;
            string v6 = authority[1..close];
            if (!System.Net.IPAddress.TryParse(v6, out _))
                return false;
            string rest = authority[(close + 1)..];
            if (rest.Length == 0)
            {
                host = v6.ToLowerInvariant();
                port = 443;
                return true;
            }
            if (rest[0] == ':')
                rest = rest[1..];
            host = v6.ToLowerInvariant();
            port = ParsePort(rest);
            return port > 0;
        }

        int colon = authority.LastIndexOf(':');
        if (colon == 0) // ":443" — empty host
            return false;
        if (colon > 0)
        {
            host = authority[..colon].ToLowerInvariant().TrimEnd('.');
            port = ParsePort(authority[(colon + 1)..]);
            return host.Length > 0 && port > 0;
        }

        // bare host (CONNECT requires a port per RFC, but be forgiving => default 443)
        host = authority.ToLowerInvariant().TrimEnd('.');
        port = 443;
        return host.Length > 0;
    }

    private static int ParsePort(string s)
        => int.TryParse(s, out int p) && p > 0 && p <= 65535 ? p : -1;

    private static List<byte[]> SplitLines(ReadOnlySpan<byte> head)
    {
        var result = new List<byte[]>();
        int start = 0;
        while (start < head.Length)
        {
            int crlf = head[start..].IndexOf((ReadOnlySpan<byte>)"\r\n"u8);
            if (crlf < 0)
            {
                result.Add(head[start..].ToArray());
                break;
            }
            result.Add(head.Slice(start, crlf).ToArray());
            start += crlf + 2;
        }
        return result;
    }

    private static bool ContainsLoneLineFeed(ReadOnlySpan<byte> b)
    {
        for (int i = 0; i < b.Length; i++)
            if (b[i] == (byte)'\n' && (i == 0 || b[i - 1] != (byte)'\r'))
                return true;
        return false;
    }

    private static ParsedProxyRequest Fail(ProxyRequestParseError error, string detail)
        => new()
        {
            Kind = ProxyRequestKind.Plain, // not reached by router when Error != None
            Method = string.Empty,
            Host = string.Empty,
            Port = 0,
            TargetUri = null,
            BodyOffset = 0,
            ForwardHeaders = Array.Empty<KeyValuePair<string, string>>(),
            Error = error,
            ErrorDetail = detail,
        };
}