// Step 04: a small loopback origin for manual/curl proxy testing.
//  - an HTTP request line -> returns a small HTML page
//  - anything else (tunnel bytes) -> echoes "<ECHO:...>"
// usage: EchoServer [--port <port>]   (default 0 => ephemeral, prints the bound port)
using System.Net;
using System.Net.Sockets;
using System.Text;

int requestedPort = 0;
for (int i = 0; i < args.Length - 1; i++)
    if (args[i] == "--port" && int.TryParse(args[i + 1], out int p)) requestedPort = p;

var listener = new TcpListener(IPAddress.Any, requestedPort);
listener.Start();
Console.WriteLine($"EchoServer listening on 0.0.0.0:{((IPEndPoint)listener.LocalEndpoint).Port}");
Console.WriteLine("Responds to HTTP with a page, echoes tunnel bytes.");

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
        string firstLine = await ReadLineAsync(s);
        if (string.IsNullOrEmpty(firstLine))
            return;

        if (firstLine.Contains("HTTP/", StringComparison.OrdinalIgnoreCase))
        {
            string body = "<html><body><h1>EchoServer</h1><p>hello!</p></body></html>";
            byte[] response = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}");
            await s.WriteAsync(response);
            await s.FlushAsync();
        }
        else
        {
            byte[] echo = Encoding.ASCII.GetBytes("ECHO:" + firstLine + "\r\n");
            await s.WriteAsync(echo);
            await s.FlushAsync();
        }
    }
}

static async Task<string> ReadLineAsync(Stream s)
{
    var sb = new StringBuilder();
    byte[] one = new byte[1];
    while (sb.Length < 1024)
    {
        int n = await s.ReadAsync(one);
        if (n == 0) break;
        sb.Append((char)one[0]);
        if (sb.Length >= 2 && sb[^2] == '\r' && sb[^1] == '\n') break;
    }
    return sb.ToString().TrimEnd('\r', '\n');
}