using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;
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
        SingBoxDohEndpointDefinition[] endpoints = EgressDohConfiguration.Endpoints.ToArray();
        string configPath = Path.Combine(root, "config.json");
        File.WriteAllText(configPath, CreateConfig(port, secret, endpoints, useDomainServer: true));

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

    [Theory]
    [InlineData("Cloudflare", "dns-cloudflare-live", "cloudflare-dns.com", "doh-live-cloudflare.egresscontroller.invalid")]
    [InlineData("DNSPod", "dns-dnspod-live", "doh.pub", "doh-live-dnspod.egresscontroller.invalid")]
    public async Task Domain_doh_endpoint_can_be_resolved_before_proxy_dial(
        string provider,
        string tag,
        string serverName,
        string probeSuffix)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("EGRESS_LIVE_DOH_DOMAIN_TEST"), "1", StringComparison.Ordinal))
            Assert.Skip("set EGRESS_LIVE_DOH_DOMAIN_TEST=1 to run the domain-address DoH transport smoke test.");

        string? executable = FindOnPath("sing-box.exe");
        if (executable is null)
            Assert.Skip("sing-box.exe is not installed on PATH.");

        string root = Path.Combine(Path.GetTempPath(), "EgressController.DohDomainLiveTests", provider, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        int port = GetFreePort();
        string secret = "doh-domain-live-test-" + tag;
        var endpoint = new SingBoxDohEndpointDefinition(
            tag,
            provider,
            IsFallback: false,
            serverName,
            443,
            "/dns-query",
            serverName,
            EgressProfileCompiler.EsimDirectTag,
            probeSuffix);
        string configPath = Path.Combine(root, "config.json");
        File.WriteAllText(configPath, CreateConfig(port, secret, [endpoint], useDomainServer: true));

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
            await WaitForVersionAsync(client, timeout.Token);
            SingBoxDnsResponse response = await client.QueryDnsAsync(
                endpoint.CreateProbeHost(Guid.NewGuid().ToString("N")),
                "A",
                timeout.Token);
            Assert.True(response.Status is 0 or 3, $"{endpoint.Tag} returned DNS status {response.Status}.");
        }
        catch (Exception exception)
        {
            string output = await ReadProcessOutputAsync(process, stdout, stderr);
            throw new InvalidOperationException(exception.Message + Environment.NewLine + output, exception);
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch { }

            try { await process.WaitForExitAsync(TestContext.Current.CancellationToken); } catch { }
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
        IReadOnlyList<SingBoxDohEndpointDefinition> endpoints,
        bool useDomainServer = false)
    {
        string servers = string.Join(
            ",\n",
            endpoints.Select(endpoint =>
                $"{{\"type\":\"https\",\"tag\":\"{endpoint.Tag}\",\"server\":\"{(useDomainServer ? endpoint.ServerName : endpoint.Server)}\",\"server_port\":{endpoint.ServerPort},\"path\":\"{endpoint.Path}\",\"tls\":{{\"enabled\":true,\"server_name\":\"{endpoint.ServerName}\"}},\"detour\":\"{endpoint.Detour}\"{(useDomainServer ? ",\"domain_resolver\":\"bootstrap\"" : string.Empty)}}}"));
        string rules = string.Join(
            ",\n",
            endpoints.Select(endpoint =>
                $"{{\"domain_suffix\":[\"{endpoint.ProbeSuffix}\"],\"action\":\"route\",\"server\":\"{endpoint.Tag}\"}}"));
        string bootstrap = useDomainServer
            ? "{\"type\":\"local\",\"tag\":\"bootstrap\"},"
            : string.Empty;
        string defaultDomainResolver = useDomainServer ? "bootstrap" : endpoints[0].Tag;
        string interfaceName = JsonEncodedText.Encode(GetDefaultInterfaceName()).ToString();
        string directOutbound = $"{{\"type\":\"direct\",\"tag\":\"{EgressProfileCompiler.EsimDirectTag}\",\"bind_interface\":\"{interfaceName}\"}}";

        return "{\n"
            + "  \"log\": { \"level\": \"error\" },\n"
            + $"  \"dns\": {{\"servers\": [{bootstrap}{servers}],\"rules\": [{rules}],\"final\": \"{endpoints[0].Tag}\",\"strategy\": \"ipv4_only\"}},\n"
            + $"  \"outbounds\": [{directOutbound}],\n"
            + $"  \"route\": {{\"final\":\"{EgressProfileCompiler.EsimDirectTag}\",\"default_domain_resolver\":\"{defaultDomainResolver}\"}},\n"
            + $"  \"experimental\": {{\"clash_api\":{{\"external_controller\":\"127.0.0.1:{port}\",\"secret\":\"{secret}\"}}}}\n"
            + "}\n";
    }

    private static string GetDefaultInterfaceName()
    {
        NetworkInterface[] active = NetworkInterface.GetAllNetworkInterfaces()
            .Where(networkInterface => networkInterface.OperationalStatus == OperationalStatus.Up
                && networkInterface.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .ToArray();
        string? requested = Environment.GetEnvironmentVariable("EGRESS_LIVE_DOH_INTERFACE");
        if (!string.IsNullOrWhiteSpace(requested))
        {
            return active.FirstOrDefault(networkInterface =>
                    string.Equals(networkInterface.Name, requested, StringComparison.OrdinalIgnoreCase))?.Name
                ?? throw new InvalidOperationException($"Requested DoH live-test interface '{requested}' is not active.");
        }

        return active
            .OrderByDescending(networkInterface => networkInterface.GetIPProperties().GatewayAddresses.Count > 0)
            .Select(networkInterface => networkInterface.Name)
            .FirstOrDefault()
            ?? throw new InvalidOperationException("No active network interface is available for the DoH live test.");
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
