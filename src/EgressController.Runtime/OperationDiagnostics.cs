using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EgressController.Runtime;

/// <summary>Bounded, best-effort lifecycle diagnostics. Never serializes configuration or arbitrary exception messages.</summary>
public sealed class OperationDiagnostics(ILifecycleLog? log)
{
    public void Record(string kind, string detail)
    {
        try { log?.Append(new(DateTimeOffset.UtcNow, kind, SanitizeOutput(detail))); }
        catch (Exception) { /* A log failure cannot change installation, recovery or protection. */ }
    }
    public void Failure(string operation, Exception error)
    { try { Record(operation + ".failed", Describe(error)); } catch (Exception) { } }
    public static string Describe(Exception error)
    {
        var result = new StringBuilder();
        for (Exception? current = error; current is not null && result.Length < 3072; current = current.InnerException)
        {
            if (result.Length > 0) result.Append("; inner=");
            result.Append(current.GetType().Name).Append(" hresult=0x").Append(current.HResult.ToString("X8"));
            if (current is RuntimeFailureException known) result.Append(" code=").Append(known.Code).Append(' ').Append(known.DiagnosticDetail);
            if (current is ResourcePreparationException resource) result.Append(" resource=").Append(resource.ResourceId).Append(" exit=").Append(resource.InterfaceId)
                .Append(" port=").Append(resource.RemotePort).Append(" stage=").Append(resource.Stage).Append(" timeout=").Append(resource.IsTimeout).Append(" proxy-failure=").Append(resource.ProxyFailure);
            if (current is ResourcePreparationException { TransportFailure: { } transport } prepared)
                result.Append(" interface=").Append(prepared.BsdName).Append(" transport-stage=").Append(transport.TransportStage).Append(" code=").Append(transport.Code).Append(" http-status=").Append(transport.HttpStatus);
            if (current is JsonException json) result.Append(" json-path=").Append(json.Path).Append(" line=").Append(json.LineNumber).Append(" byte=").Append(json.BytePositionInLine);
            if (current is Win32Exception native) result.Append(" native-code=").Append(native.NativeErrorCode);
            if (current is System.Net.Sockets.SocketException socket) result.Append(" socket=").Append(socket.SocketErrorCode);
            if (current.StackTrace is { } trace) result.Append(" at=").Append(string.Join(" | ", trace.Split('\n').Take(4)));
        }
        return SanitizeOutput(result.ToString());
    }
    // Only use for the fixed installer/launchctl output, never for core streams or configuration dumps.
    public static string SanitizeOutput(string value)
    {
        try
        {
            value = value.Length > 8192 ? value[^8192..] : value;
            value = Regex.Replace(value, @"\b[a-zA-Z][a-zA-Z0-9+.-]*://[^\s]+", "[redacted-url]", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            value = Regex.Replace(value, @"(?i)\b(password|token|secret|authorization)\s*[:=]\s*[^\s;]+", "$1=[redacted]", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            return new string(value.Select(c => char.IsControl(c) ? ' ' : c).ToArray());
        }
        catch (Exception) { return "[diagnostic text unavailable]"; }
    }
}
