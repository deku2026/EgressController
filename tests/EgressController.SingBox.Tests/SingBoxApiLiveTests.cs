using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using EgressController.SingBox.Api;
using EgressController.SingBox.Api.Models;

namespace EgressController.SingBox.Tests;

/// <summary>Runs the installed core without TUN and proves the real controller REST/WS surface.</summary>
public sealed class SingBoxApiLiveTests
{
    [Fact]
    public async Task Installed_core_serves_authenticated_rest_and_websocket_diagnostics()
    {
        string? executable = FindOnPath("sing-box.exe");
        if (executable is null)
            Assert.Skip("sing-box.exe is not installed on PATH.");

        string root = Path.Combine(Path.GetTempPath(), "EgressController.SingBoxApiTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        int port = GetFreePort();
        const string secret = "live-api-test-secret";
        string configPath = Path.Combine(root, "config.json");
        File.WriteAllText(
            configPath,
            $$"""
            {
              "log": { "level": "info" },
              "dns": {
                "servers": [ { "type": "local", "tag": "local" } ],
                "final": "local"
              },
              "outbounds": [ { "type": "direct", "tag": "direct" } ],
              "route": { "final": "direct" },
              "experimental": {
                "clash_api": {
                  "external_controller": "127.0.0.1:{{port}}",
                  "secret": "{{secret}}"
                }
              }
            }
            """,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = Path.GetDirectoryName(executable) ?? Environment.CurrentDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        process.StartInfo.ArgumentList.Add("run");
        process.StartInfo.ArgumentList.Add("-c");
        process.StartInfo.ArgumentList.Add(configPath);
        Assert.True(process.Start());
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);

        using var client = new SingBoxApiClient(new Uri($"http://127.0.0.1:{port}"), secret);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            SingBoxVersionResponse version = await WaitForVersionAsync(client, timeout.Token);
            Assert.StartsWith("sing-box ", version.Version, StringComparison.Ordinal);

            SingBoxConfigResponse config = await client.GetConfigAsync(timeout.Token);
            Assert.NotNull(config);
            SingBoxRulesResponse rules = await client.GetRulesAsync(timeout.Token);
            Assert.NotNull(rules);
            SingBoxConnectionsResponse connections = await client.GetConnectionsAsync(timeout.Token);
            Assert.Empty(connections.Connections);

            SingBoxDnsResponse dns = await client.QueryDnsAsync("localhost", "A", timeout.Token);
            Assert.Equal(0, dns.Status);
            await client.FlushDnsCacheAsync(timeout.Token);
            await client.FlushFakeIpCacheAsync(timeout.Token);
            await client.CloseAllConnectionsAsync(timeout.Token);

            using ClientWebSocket connectionsSocket = await client.ConnectConnectionsWebSocketAsync(250, timeout.Token);
            string? connectionMessage = await SingBoxApiClient.ReceiveTextMessageAsync(connectionsSocket, timeout.Token);
            Assert.NotNull(connectionMessage);
            using (JsonDocument connectionJson = JsonDocument.Parse(connectionMessage!))
                Assert.True(connectionJson.RootElement.TryGetProperty("connections", out _));

            using ClientWebSocket trafficSocket = await client.ConnectTrafficWebSocketAsync(timeout.Token);
            using var trafficTimeout = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            trafficTimeout.CancelAfter(TimeSpan.FromSeconds(3));
            string? trafficMessage = await SingBoxApiClient.ReceiveTextMessageAsync(trafficSocket, trafficTimeout.Token);
            Assert.NotNull(trafficMessage);
            using (JsonDocument trafficJson = JsonDocument.Parse(trafficMessage!))
            {
                Assert.True(trafficJson.RootElement.TryGetProperty("up", out _));
                Assert.True(trafficJson.RootElement.TryGetProperty("down", out _));
            }

            using ClientWebSocket logsSocket = await client.ConnectLogsWebSocketAsync("info", timeout.Token);
            Assert.Equal(WebSocketState.Open, logsSocket.State);
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch { }

            try
            {
                await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            }
            catch { }

            string error = await stderr;
            string output = await stdout;
            _ = error;
            _ = output;

            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<SingBoxVersionResponse> WaitForVersionAsync(
        SingBoxApiClient client,
        CancellationToken cancellationToken)
    {
        SingBoxApiException? last = null;
        while (true)
        {
            try
            {
                return await client.GetVersionAsync(cancellationToken);
            }
            catch (SingBoxApiException exception) when (exception.StatusCode is null)
            {
                last = exception;
            }

            await Task.Delay(100, cancellationToken);
            if (last is not null && cancellationToken.IsCancellationRequested)
                throw last;
        }
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string? FindOnPath(string fileName)
        => Environment.GetEnvironmentVariable("PATH")?
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory, fileName))
            .FirstOrDefault(File.Exists);
}
