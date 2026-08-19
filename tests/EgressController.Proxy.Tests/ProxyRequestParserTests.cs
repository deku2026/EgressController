using System.Text;
using EgressController.Proxy.Parsing;

namespace EgressController.Proxy.Tests;

public class ProxyRequestParserTests
{
    private static ParsedProxyRequest Parse(string raw)
        => ProxyRequestParser.Parse(Encoding.ASCII.GetBytes(raw));

    private static string WithBody(string head, string body)
        => head + body;

    [Fact]
    public void Converts_authority_form()
    {
        var r = Parse("CONNECT example.com:443 HTTP/1.1\r\nHost: example.com:443\r\n\r\n");
        Assert.Equal(ProxyRequestKind.Connect, r.Kind);
        Assert.Equal("example.com", r.Host);
        Assert.Equal(443, r.Port);
    }

    [Fact]
    public void Converts_ipv6_authority()
    {
        var r = Parse("CONNECT [::1]:8443 HTTP/1.1\r\nHost: [::1]:8443\r\n\r\n");
        Assert.Equal(ProxyRequestKind.Connect, r.Kind);
        Assert.Equal("::1", r.Host);
        Assert.Equal(8443, r.Port);
    }

    [Fact]
    public void Converts_with_bare_host_defaults_to_443()
    {
        var r = Parse("CONNECT example.com HTTP/1.1\r\n\r\n");
        Assert.Equal(ProxyRequestKind.Connect, r.Kind);
        Assert.Equal("example.com", r.Host);
        Assert.Equal(443, r.Port);
    }

    [Theory]
    [InlineData("CONNECT :443 HTTP/1.1\r\n\r\n")]             // empty host
    [InlineData("CONNECT example.com:99999 HTTP/1.1\r\n\r\n")] // bad port
    [InlineData("CONNECT [::1 HTTP/1.1\r\n\r\n")]              // unclosed bracket
    public void Rejects_malformed_authority(string raw)
    {
        Assert.Equal(ProxyRequestParseError.InvalidAuthority, Parse(raw).Error);
    }

    [Fact]
    public void Space_in_authority_is_a_malformed_request_line()
    {
        Assert.Equal(ProxyRequestParseError.InvalidRequestLine, Parse("CONNECT exa mple HTTP/1.1\r\n\r\n").Error);
    }

    [Fact]
    public void Plain_absolute_form_parses_host_from_uri()
    {
        var r = Parse("GET http://example.com/path?q=1 HTTP/1.1\r\nHost: example.com\r\n\r\n");
        Assert.Equal(ProxyRequestKind.Plain, r.Kind);
        Assert.Equal("example.com", r.Host);
        Assert.Equal(80, r.Port);
        Assert.Equal("/path?q=1", r.TargetUri!.PathAndQuery);
    }

    [Fact]
    public void Plain_host_header_mismatch_is_rejected()
    {
        var r = Parse("GET http://example.com/x HTTP/1.1\r\nHost: evil.com\r\n\r\n");
        Assert.Equal(ProxyRequestParseError.HostHeaderMismatch, r.Error);
    }

    [Fact]
    public void Hop_by_hop_and_proxy_credentials_are_stripped()
    {
        var r = Parse(
            "GET http://example.com/x HTTP/1.1\r\n" +
            "Host: example.com\r\n" +
            "Proxy-Authorization: Basic abc123\r\n" +
            "Proxy-Connection: keep-alive\r\n" +
            "Connection: close\r\n" +
            "Content-Length: 5\r\n\r\nhello");

        Assert.Equal(ProxyRequestParseError.None, r.Error);
        Assert.DoesNotContain(r.ForwardHeaders, h => h.Key.Equals("proxy-authorization", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(r.ForwardHeaders, h => h.Key.Equals("proxy-connection", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(r.ForwardHeaders, h => h.Key.Equals("connection", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(r.ForwardHeaders, h => h.Key.Equals("content-length", StringComparison.OrdinalIgnoreCase) && h.Value == "5");
    }

    [Fact]
    public void Body_offset_points_past_header_section()
    {
        string request = "POST http://example.com/e HTTP/1.1\r\nHost: example.com\r\nContent-Length: 4\r\n\r\nbody";
        var r = Parse(request);
        Assert.Equal(ProxyRequestParseError.None, r.Error);
        Assert.Equal(Encoding.ASCII.GetBytes(request).Length - 4, r.BodyOffset);
    }

    [Fact]
    public void Lone_lf_is_rejected_as_malformed_framing()
    {
        var r = Parse("CONNECT example.com:443 HTTP/1.1\nHost: x\n\n");
        Assert.NotEqual(ProxyRequestParseError.None, r.Error);
    }

    [Fact]
    public void Unsupported_method_is_rejected()
    {
        var r = Parse("FOOBAR http://example.com/x HTTP/1.1\r\nHost: example.com\r\n\r\n");
        Assert.Equal(ProxyRequestParseError.UnsupportedMethod, r.Error);
    }

    [Fact]
    public void Redirect_bytes_too_short_when_head_incomplete()
    {
        var r = Parse("CONNECT example.com:443 HTTP/1.1\r\nHost");
        Assert.Equal(ProxyRequestParseError.BytesTooShort, r.Error);
    }

    [Fact]
    public void Header_value_crlf_injection_is_rejected()
    {
        // "b" is a bare token with no ':' — malformed header field → rejected, not smuggled.
        var r = Parse("GET http://example.com/x HTTP/1.1\r\nHost: example.com\r\nX-Evil: a\r\nb\r\n\r\n");
        Assert.Equal(ProxyRequestParseError.MalformedHeaderField, r.Error);
    }
}