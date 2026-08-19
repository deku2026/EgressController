// Step 04: a loopback HTTP-compatible upstream DOUBLE for local proxy testing.
//  - CONNECT host:port  -> opens the target (loopback) and relays (like a minimal proxy)
//  - plain absolute-form -> returns a canned 200 origin-like body
// usage: FakeUpstream [--port <port>]   (default 0 => ephemeral, prints bound port)
using System.Net;
using System.Net.Sockets;
using System.Text;

int requestedPort = 0;
for (int i = 0; i < args.Length - 1; i++)
    if (args[i] == "--port" && int.TryParse(args[i + 1], out int p)) requestedPort = p;

var listener = new TcpListener(IPAddress.Any, requestedPort);
listener.Start();
Console.WriteLine($"FakeUpstream listening on 0.0.0.0:{((IPEndPoint)listener.LocalEndpoint).Port}");

while (true)
{
    TcpClient client = await listener.AcceptTcpClientAsync();
    _ = HandleAsync(client);
}

static async Task HandleAsync(TcpClient client)
{
    using (client)
    using (var s = client.GetStream())
    {
        byte[] head = await ReadRawHeadAsync(s);
        string text = Encoding.ASCII.GetString(head);
        string firstLine = text.Split('\r')[0];
        string[] parts = firstLine.Split(' ');

        if (parts.Length >= 2 && parts[0] == "CONNECT")
        {
            string authority = parts[1];
            int colon = authority.LastIndexOf(':');
            int port = int.Parse(authority[(colon + 1)..]);
            using var target = new TcpClient();
            await target.ConnectAsync(IPAddress.Loopback, port);
            var t = target.GetStream();
            await s.WriteAsync("HTTP/1.1 200 Connection established\r\n\r\n"u8.ToArray());
            await s.FlushAsync();
            var a = s.CopyToAsync(t);
            var b = t.CopyToAsync(s);
            await Task.WhenAny(a, b);
        }
        else
        {
            await s.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 6\r\nConnection: close\r\n\r\nhello!"u8.ToArray());
            await s.FlushAsync();
        }
    }
}

static async Task<byte[]> ReadRawHeadAsync(Stream s)
{
    var buffer = new MemoryStream();
    byte[] tmp = new byte[1024];
    while (buffer.Length <= 64 * 1024)
    {
        byte[] cur = buffer.ToArray();
        if (cur.Length >= 4 && cur.AsSpan(^4..).SequenceEqual("\r\n\r\n"u8))
            return cur;
        int n = await s.ReadAsync(tmp);
        if (n == 0) return buffer.ToArray();
        buffer.Write(tmp, 0, n);
    }
    return buffer.ToArray();
}