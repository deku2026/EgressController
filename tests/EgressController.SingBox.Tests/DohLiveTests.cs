using System.Diagnostics;
using System.Net;
using EgressController.SingBox.Api;
using EgressController.SingBox.Api.Models;
using EgressController.SingBox.Configuration;

namespace EgressController.SingBox.Tests;

public sealed class DohLiveTests
{
    [Fact]
    public async Task Configured_doh_endpoints_answer_through_their_sing_box_dns_rules()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("EGRESS_LIVE_DOH_TEST"), "1", StringComparison.Ordinal))
            Assert.Skip("set EGRESS_LIVE_DOH_TEST=1 to run the realtime DoH transport smoke test.");

        string? executable = FindOnPath("sing-box.exe");
        if (executable is null)
            Assert.Skip("sing-box.exe is not installed on PATH.");

        string root = Path.Combine(Path.GetTempPath(), "EgressController.DohLiveTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        int port = GetFreePort();
        const string secret = "doh-live-test-secret";
        SingBoxDohEndpointDefinition[] endpoints = EgressDohConfiguration.Endpoints
            .Where(endpoint => endpoint.RoutePlane == DohRoutePlane.Clash)
            .ToArray();
        string configPath = Path.Combine(root, "config.json");
        File.WriteAllText(configPath, CreateConfig(port, secret, endpoints));

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
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            try
            {
                await WaitForVersionAsync(client, timeout.Token);
            }
            catch (Exception exception)
            {
                string output = await ReadProcessOutputAsync(process, stdout, stderr);
                throw new InvalidOperationException(exception.Message + Environment.NewLine + output, exception);
            }
            foreach (SingBoxDohEndpointDefinition endpoint in endpoints)
            {
                SingBoxDnsResponse response = await client.QueryDnsAsync(
                    endpoint.CreateProbeHost(Guid.NewGuid().ToString("N")),
                    "A",
                    timeout.Token);
                Assert.True(
                    response.Status is 0 or 3,
                    $"{endpoint.Tag} returned DNS status {response.Status}.");
            }
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

            _ = await stdout;
            _ = await stderr;
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<string> ReadProcessOutputAsync(
        Process process,
        Task<string> stdout,
        Task<string> stderr)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch { }

        try { await process.WaitForExitAsync(); } catch { }
        return "stdout:" + await stdout + Environment.NewLine + "stderr:" + await stderr;
    }

    private static string CreateConfig(
        int port,
        string secret,
        IReadOnlyList<SingBoxDohEndpointDefinition> endpoints)
    {
        string servers = string.Join(
            ",\n",
            endpoints.Select(endpoint =>
                $"{{\"type\":\"https\",\"tag\":\"{endpoint.Tag}\",\"server\":\"{endpoint.Server}\",\"server_port\":{endpoint.ServerPort},\"path\":\"{endpoint.Path}\",\"tls\":{{\"enabled\":true,\"server_name\":\"{endpoint.ServerName}\"}},\"detour\":\"{endpoint.Detour}\"}}"));
        string rules = string.Join(
            ",\n",
            endpoints.Select(endpoint =>
                $"{{\"domain_suffix\":[\"{endpoint.ProbeSuffix}\"],\"action\":\"route\",\"server\":\"{endpoint.Tag}\"}}"));

        return "{\n"
            + "  \"log\": { \"level\": \"error\" },\n"
            + $"  \"dns\": {{\"servers\": [{servers}],\"rules\": [{rules}],\"final\": \"{EgressDohConfiguration.ClashCloudflareTag}\",\"strategy\": \"ipv4_only\"}},\n"
            + $"  \"outbounds\": [{{\"type\":\"socks\",\"tag\":\"{EgressProfileCompiler.UpstreamSocksTag}\",\"server\":\"127.0.0.1\",\"server_port\":7890,\"version\":\"5\"}}],\n"
            + $"  \"route\": {{\"final\":\"{EgressProfileCompiler.UpstreamSocksTag}\",\"default_domain_resolver\":\"{EgressDohConfiguration.ClashCloudflareTag}\"}},\n"
            + $"  \"experimental\": {{\"clash_api\":{{\"external_controller\":\"127.0.0.1:{port}\",\"secret\":\"{secret}\"}}}}\n"
            + "}\n";
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
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string? FindOnPath(string fileName)
        => Environment.GetEnvironmentVariable("PATH")?
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory, fileName))
            .FirstOrDefault(File.Exists);
}
