using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using EgressController.Policy;

namespace EgressController.Runtime;

public interface IBoundDnsExchange
{
    ValueTask<byte[]> ExchangeAsync(byte[] query, IPAddress server, InterfaceBinding binding, bool tcp, CancellationToken token);
}

/// <summary>Literal DNS upstream, all UDP and TCP sockets physically bound before sending.</summary>
public sealed class BoundDnsSocketExchange(IPhysicalSocketBinder binder) : IBoundDnsExchange
{
    public async ValueTask<byte[]> ExchangeAsync(byte[] query, IPAddress server, InterfaceBinding binding, bool tcp, CancellationToken token)
    {
        using var socket = new Socket(server.AddressFamily, tcp ? SocketType.Stream : SocketType.Dgram, tcp ? ProtocolType.Tcp : ProtocolType.Udp);
        binder.Bind(socket, binding);
        await socket.ConnectAsync(new IPEndPoint(server, 53), token).ConfigureAwait(false);
        if (!tcp)
        {
            await socket.SendAsync(query, token).ConfigureAwait(false);
            byte[] response = new byte[65535]; int count = await socket.ReceiveAsync(response, token).ConfigureAwait(false);
            return response[..count];
        }
        await using var stream = new NetworkStream(socket, ownsSocket: false);
        byte[] framed = new byte[query.Length + 2]; BinaryPrimitives.WriteUInt16BigEndian(framed, checked((ushort)query.Length)); query.CopyTo(framed, 2);
        await stream.WriteAsync(framed, token).ConfigureAwait(false);
        byte[] prefix = new byte[2]; await stream.ReadExactlyAsync(prefix, token).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadUInt16BigEndian(prefix); if (length < 12) throw new IOException("Bound DNS response is invalid.");
        byte[] body = new byte[length]; await stream.ReadExactlyAsync(body, token).ConfigureAwait(false); return body;
    }
}

public sealed class BoundDnsResolver(IBoundDnsExchange exchange, IPAddress upstreamIp) : IInterfaceDnsResolver
{
    public BoundDnsResolver(IPhysicalSocketBinder binder, IPAddress upstreamIp) : this(new BoundDnsSocketExchange(binder), upstreamIp) { }
    public async ValueTask<IReadOnlyList<IPAddress>> ResolveAsync(string host, InterfaceBinding binding, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out var literal)) return [literal];
        string ascii;
        try { ascii = new System.Globalization.IdnMapping().GetAscii(host.TrimEnd('.')).ToLowerInvariant(); }
        catch (ArgumentException) { throw new IOException("Bound DNS hostname is invalid."); }
        if (ascii.Length is < 1 or > 253 || ascii.Split('.').Any(label => label.Length is < 1 or > 63 || !label.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '-')))
            throw new IOException("Bound DNS hostname is invalid.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var addresses = new List<IPAddress>();
        foreach (ushort type in new ushort[] { 1, 28 })
        {
            ushort id = (ushort)RandomNumberGenerator.GetInt32(65536); byte[] query = Query(ascii, type, id);
            var response = await exchange.ExchangeAsync(query, upstreamIp, binding, false, timeout.Token).ConfigureAwait(false);
            CheckHeader(response, id);
            if ((response[2] & 2) != 0) response = await exchange.ExchangeAsync(query, upstreamIp, binding, true, timeout.Token).ConfigureAwait(false);
            addresses.AddRange(Parse(response, ascii, type, id));
        }
        return addresses.Distinct().Take(64).ToArray();
    }
    private static byte[] Query(string host, ushort type, ushort id)
    {
        using var stream = new MemoryStream(); byte[] header = new byte[12]; BinaryPrimitives.WriteUInt16BigEndian(header, id); header[2] = 1; header[5] = 1; stream.Write(header);
        foreach (string label in host.Split('.')) { stream.WriteByte((byte)label.Length); stream.Write(Encoding.ASCII.GetBytes(label)); }
        stream.WriteByte(0); stream.WriteByte(0); stream.WriteByte((byte)type); stream.WriteByte(0); stream.WriteByte(1); return stream.ToArray();
    }
    private static void CheckHeader(byte[] response, ushort id)
    {
        if (response.Length < 12 || BinaryPrimitives.ReadUInt16BigEndian(response) != id || (response[2] & 0xf8) != 0x80 || (response[3] & 15) != 0 || BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(4)) != 1)
            throw new IOException("Bound DNS response failed validation.");
    }
    private static IReadOnlyList<IPAddress> Parse(byte[] response, string host, ushort requestedType, ushort id)
    {
        CheckHeader(response, id); if ((response[2] & 2) != 0) throw new IOException("Bound DNS TCP response is truncated.");
        int offset = 12; string question = Name(response, ref offset);
        if (question != host || offset + 4 > response.Length || BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(offset)) != requestedType || BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(offset + 2)) != 1) throw new IOException("Bound DNS question mismatch.");
        offset += 4; int answers = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(6)); if (answers > 1024) throw new IOException("Bound DNS answer limit.");
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal); var records = new List<(string Owner, IPAddress Address)>();
        for (int index = 0; index < answers; index++)
        {
            string owner = Name(response, ref offset);
            if (offset + 10 > response.Length) throw new IOException("Bound DNS answer truncated.");
            ushort type = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(offset)); ushort cls = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(offset + 2)); int length = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(offset + 8)); offset += 10;
            if (offset + length > response.Length) throw new IOException("Bound DNS data truncated.");
            if (cls == 1 && type == 5)
            {
                int aliasOffset = offset; string target = Name(response, ref aliasOffset); if (aliasOffset != offset + length || !aliases.TryAdd(owner, target)) throw new IOException("Bound DNS alias invalid.");
            }
            if (cls == 1 && type == requestedType && length == (type == 1 ? 4 : 16)) records.Add((owner, new IPAddress(response.AsSpan(offset, length))));
            offset += length;
        }
        var names = new HashSet<string>(StringComparer.Ordinal) { host }; string current = host;
        while (aliases.TryGetValue(current, out var target)) { if (names.Count > 32 || !names.Add(target)) throw new IOException("Bound DNS alias loop."); current = target; }
        return records.Where(record => names.Contains(record.Owner)).Select(record => record.Address).ToArray();
    }
    private static string Name(byte[] bytes, ref int offset)
    {
        int cursor = offset; bool jumped = false; var labels = new List<string>(); var seen = new HashSet<int>();
        for (int count = 0; count < 128; count++)
        {
            if (cursor >= bytes.Length || !seen.Add(cursor)) throw new IOException("Bound DNS compression invalid.");
            int length = bytes[cursor++];
            if (length == 0) { if (!jumped) offset = cursor; string name = string.Join('.', labels); if (name.Length > 253) throw new IOException("Bound DNS name too long."); return name; }
            if ((length & 0xc0) == 0xc0) { if (cursor >= bytes.Length) throw new IOException("Bound DNS pointer truncated."); int target = ((length & 63) << 8) | bytes[cursor++]; if (!jumped) offset = cursor; jumped = true; cursor = target; continue; }
            if (length > 63 || cursor + length > bytes.Length) throw new IOException("Bound DNS label invalid.");
            var label = Encoding.ASCII.GetString(bytes, cursor, length).ToLowerInvariant(); if (!label.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_')) throw new IOException("Bound DNS label invalid."); labels.Add(label); cursor += length;
        }
        throw new IOException("Bound DNS compression limit.");
    }
}
