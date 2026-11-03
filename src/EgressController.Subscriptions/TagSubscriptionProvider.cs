using EgressController.Core.Protection;
using EgressController.Core.Storage;
using PuppeteerSharp;

namespace EgressController.Subscriptions;

/// <summary>Verified TAG v2 controls. A closed 15-minute subscription window is not an expired login.</summary>
public sealed class TagSubscriptionProvider : IInteractiveSubscriptionProvider
{
    public async Task<LoginState> LoginAsync(SubscriptionSettings settings, ProviderCredentials credentials, string browserDirectory, IProgress<SubscriptionProgress> progress, CancellationToken token)
    {
        RuntimeSafety.RequireLiveOperations();
        ValidateSettings(settings);
        PrivateFile.EnsureDirectory(browserDirectory);
        await using var ownership = new FileStream(Path.Combine(browserDirectory, "egress-session.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        // First login is a normal Chrome process: no CDP, automation flags, injected scripts or credential submission.
        var start = new System.Diagnostics.ProcessStartInfo(settings.ChromePath) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--user-data-dir=" + browserDirectory);
        start.ArgumentList.Add("--new-window");
        start.ArgumentList.Add("https://tagss.pro/#/home");
        using var process = System.Diagnostics.Process.Start(start) ?? throw new IOException("无法打开专属 Chrome 登录窗口。");
        _ = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
        _ = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
        progress.Report(new("请在普通 Chrome 中登录", "完成登录后请退出这个专属 Chrome（macOS 使用 ⌘Q），然后点击“检查会话”；窗口不会自动关闭", true));
        // Wait for normal Chrome shutdown so profile writes are complete. Cancelling never kills a manual browser.
        await process.WaitForExitAsync(token).ConfigureAwait(false);
        return new(LoginStatus.Unknown, DateTimeOffset.UtcNow, "专属 Chrome 已退出，会话已保留；请检查会话以确认登录是否有效");
    }

    public Task<LoginState> CheckSessionAsync(SubscriptionSettings settings, string browserDirectory, CancellationToken token)
        => WithPageAsync(settings, browserDirectory, async page =>
        { await NavigateHomeAsync(page, token).ConfigureAwait(false); return await ObserveSessionAsync(page, token).ConfigureAwait(false); }, token);

    public Task<IReadOnlyList<CapturedSubscription>> CaptureAsync(SubscriptionSettings settings, ProviderCredentials credentials,
        string browserDirectory, IProgress<SubscriptionProgress> progress, CancellationToken token)
        => WithPageAsync<IReadOnlyList<CapturedSubscription>>(settings, browserDirectory, async page =>
        {
            progress.Report(new("检查登录会话", "复用专属 Chrome Profile，确认登录状态"));
            await NavigateHomeAsync(page, token).ConfigureAwait(false);
            var login = await ObserveSessionAsync(page, token).ConfigureAwait(false);
            if (login.Status != LoginStatus.Valid) throw new LoginRequiredException(login.Status, login.Detail);
            await DismissAnnouncementAsync(page).ConfigureAwait(false);
            progress.Report(new("开启订阅窗口", "按网站要求开启本次 15 分钟订阅窗口"));
            var open = await FindControlAsync(page, "复制 / 导入订阅").ConfigureAwait(false) ?? await FindControlAsync(page, "开启订阅").ConfigureAwait(false);
            if (open is null) throw new InvalidOperationException("TAG 订阅入口已改变，请手动检查页面。");
            await ClickReadyAsync(open, token).ConfigureAwait(false);
            await WaitForAsync(async () => await FindControlAsync(page, "开启订阅", true).ConfigureAwait(false) is not null || await FindControlAsync(page, "复制链接", true).ConfigureAwait(false) is not null, TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
            var enable = await FindControlAsync(page, "开启订阅", dialogOnly: true).ConfigureAwait(false);
            if (enable is not null) await ClickReadyAsync(enable, token).ConfigureAwait(false);
            await WaitForAsync(async () => await FindControlAsync(page, "复制链接", true).ConfigureAwait(false) is not null, TimeSpan.FromSeconds(20), token).ConfigureAwait(false);
            var results = new List<CapturedSubscription>();
            foreach (var (format, label, target) in new[] { (SourceFormat.Clash, "Clash Meta", "clashmeta"), (SourceFormat.Surge, "Surge", "surge") })
            {
                // Format buttons expose aria-label and aria-pressed. Never click the client import URI.
                var choice = await page.QuerySelectorAsync($"button[aria-label^='{label}：']").ConfigureAwait(false);
                if (choice is null) continue;
                await ClickReadyAsync(choice, token).ConfigureAwait(false);
                // Capture only the result of the user's copy-link action in this isolated page. Do not put a bearer URL on the OS clipboard.
                await page.EvaluateFunctionAsync("() => { window.__egressCopiedLink = null; navigator.clipboard.writeText = async text => { window.__egressCopiedLink = text; }; }").ConfigureAwait(false);
                var copy = await FindControlAsync(page, "复制链接", true).ConfigureAwait(false) ?? throw new InvalidOperationException("TAG 未提供复制链接按钮。");
                await ClickReadyAsync(copy, token).ConfigureAwait(false);
                string? url = null;
                await WaitForAsync(async () => { url = await page.EvaluateFunctionAsync<string?>("() => window.__egressCopiedLink").ConfigureAwait(false); return !string.IsNullOrWhiteSpace(url); }, TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
                if (!Uri.TryCreate(url, UriKind.Absolute, out var resource) || resource.Scheme != "https" || resource.UserInfo.Length > 0 || !resource.Query.Contains("target=" + target, StringComparison.Ordinal))
                    throw new InvalidDataException("订阅链接格式与所选客户端不一致。");
                progress.Report(new("获取订阅", $"正在获取网站提供的 {format} 原始文件"));
                byte[] bytes = await DownloadInBrowserAsync(page, resource, token).ConfigureAwait(false);
                results.Add(new(format, bytes));
            }
            if (results.Count == 0) throw new InvalidOperationException("网站没有提供可识别的 Clash / Surge 格式。");
            return results;
        }, token);

    private static async Task<T> WithPageAsync<T>(SubscriptionSettings settings, string browserDirectory, Func<IPage, Task<T>> work, CancellationToken token)
    {
        RuntimeSafety.RequireLiveOperations();
        ValidateSettings(settings);
        PrivateFile.EnsureDirectory(browserDirectory);
        await using var ownership = new FileStream(Path.Combine(browserDirectory, "egress-session.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        IBrowser? browser = null;
        System.Diagnostics.Process? process = null;
        try
        {
            // Use the same installed Chrome and persistent profile as manual onboarding.
            // No Puppeteer default switches, UA/fingerprint overrides, stealth scripts or automatic challenge solving.
            var reservation = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            reservation.Start(); int port = ((System.Net.IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
            var start = new System.Diagnostics.ProcessStartInfo(settings.ChromePath) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("--user-data-dir=" + browserDirectory);
            start.ArgumentList.Add("--remote-debugging-address=127.0.0.1");
            start.ArgumentList.Add("--remote-debugging-port=" + port);
            start.ArgumentList.Add("--new-window"); start.ArgumentList.Add("about:blank");
            process = System.Diagnostics.Process.Start(start) ?? throw new IOException("无法打开专属 Chrome。");
            _ = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null); _ = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
            using var endpointClient = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(2) };
            string? socket = null; var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
            while (socket is null && DateTimeOffset.UtcNow < deadline)
            {
                token.ThrowIfCancellationRequested();
                if (process.HasExited) throw new IOException("专属 Chrome 资料可能仍在使用；请退出其窗口后重试。");
                try
                {
                    using var json = System.Text.Json.JsonDocument.Parse(await endpointClient.GetStringAsync($"http://127.0.0.1:{port}/json/version", token).ConfigureAwait(false));
                    socket = json.RootElement.GetProperty("webSocketDebuggerUrl").GetString();
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !token.IsCancellationRequested) { await Task.Delay(150, token).ConfigureAwait(false); }
            }
            if (!Uri.TryCreate(socket, UriKind.Absolute, out var endpoint) || endpoint.Scheme != "ws" || endpoint.Port != port || endpoint.Host != "127.0.0.1")
                throw new IOException("无法连接专属 Chrome；请退出其窗口后重试。");
            browser = await Puppeteer.ConnectAsync(new ConnectOptions { BrowserWSEndpoint = socket, DefaultViewport = null }).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            await using var page = (await browser.PagesAsync().ConfigureAwait(false)).FirstOrDefault() ?? await browser.NewPageAsync().ConfigureAwait(false); page.DefaultTimeout = 15000;
            using var cancellation = token.Register(() => _ = browser.CloseAsync());
            return await work(page).ConfigureAwait(false);
        }
        catch (Exception ex) when (token.IsCancellationRequested && ex is PuppeteerException or TargetClosedException) { throw new OperationCanceledException(token); }
        catch (PuppeteerException) { throw new IOException("Chrome 页面操作未完成；会话已保留，请重试或手动检查登录状态。"); }
        finally
        {
            if (browser is not null)
            {
                try { await browser.CloseAsync().ConfigureAwait(false); }
                catch (PuppeteerException) { /* User may have closed the dedicated window. */ }
                await browser.DisposeAsync().ConfigureAwait(false);
            }
            process?.Dispose();
        }
    }
    private static void ValidateSettings(SubscriptionSettings settings)
    {
        if (!Uri.TryCreate(settings.Website, UriKind.Absolute, out var website) || website.Scheme != "https" || website.Host != "tagss.pro" || website.UserInfo.Length > 0)
            throw new ArgumentException("当前仅支持 https://tagss.pro/ 的 TAG Provider。");
        if (!Path.IsPathFullyQualified(settings.ChromePath) || !File.Exists(settings.ChromePath)) throw new IOException("请指定已安装的 Chrome 可执行文件。");
    }
    private static async Task NavigateHomeAsync(IPage page, CancellationToken token)
        => await page.GoToAsync("https://tagss.pro/#/home", new NavigationOptions { WaitUntil = [WaitUntilNavigation.DOMContentLoaded], Timeout = 30000 }).WaitAsync(token).ConfigureAwait(false);
    private static async Task<LoginState> ObserveSessionAsync(IPage page, CancellationToken token)
    {
        for (int i = 0; i < 30; i++)
        {
            if (await IsAuthenticatedAsync(page).ConfigureAwait(false)) return new(LoginStatus.Valid, DateTimeOffset.UtcNow, "登录有效 · 已确认账户页面");
            if (page.Url.Contains("#/auth", StringComparison.Ordinal) && await page.QuerySelectorAsync("input[type=password]").ConfigureAwait(false) is not null)
                return new(LoginStatus.NotSignedIn, DateTimeOffset.UtcNow, "网站已返回登录页，请手动登录");
            await Task.Delay(500, token).ConfigureAwait(false);
        }
        string title = await page.GetTitleAsync().ConfigureAwait(false);
        return title.Contains("moment", StringComparison.OrdinalIgnoreCase)
            ? new(LoginStatus.NeedsVerification, DateTimeOffset.UtcNow, "网站要求人工安全验证，请打开登录窗口")
            : new(LoginStatus.Unknown, DateTimeOffset.UtcNow, "暂时无法确认会话；网络或页面未就绪，不判定为过期");
    }
    private static async Task<bool> IsAuthenticatedAsync(IPage page) => page.Url.Contains("#/home", StringComparison.Ordinal) &&
        await FindControlAsync(page, "重置订阅凭据").ConfigureAwait(false) is not null;
    private static async Task DismissAnnouncementAsync(IPage page)
    {
        var dismiss = await FindControlAsync(page, "知道了").ConfigureAwait(false);
        if (dismiss is not null) await dismiss.ClickAsync().ConfigureAwait(false);
    }
    private static async Task<IElementHandle?> FindControlAsync(IPage page, string text, bool dialogOnly = false)
    {
        // TAG uses a drawer on compact windows and an inline panel on desktop widths.
        bool hasDialog = dialogOnly && await page.QuerySelectorAsync("[role=dialog]").ConfigureAwait(false) is not null;
        string selector = hasDialog ? "[role=dialog] button, [role=dialog] div.cursor-pointer" : "button, div.cursor-pointer";
        foreach (var element in await page.QuerySelectorAllAsync(selector).ConfigureAwait(false))
        {
            bool match = await element.EvaluateFunctionAsync<bool>("(e, text) => !!e.getClientRects().length && ((e.getAttribute('aria-label') || '') === text || (e.textContent || '').trim() === text || (e.tagName === 'BUTTON' && [...e.querySelectorAll('span')].some(s => s.textContent.trim() === text)))", text).ConfigureAwait(false);
            if (match) return element;
        }
        return null;
    }
    private static async Task ClickReadyAsync(IElementHandle element, CancellationToken token)
    {
        await element.ScrollIntoViewAsync().ConfigureAwait(false);
        await WaitForAsync(() => element.EvaluateFunctionAsync<bool>("e => { const r=e.getBoundingClientRect(); const hit=document.elementFromPoint(r.x+r.width/2,r.y+r.height/2); return r.width>0 && r.height>0 && hit && (e===hit || e.contains(hit)); }"), TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
        await element.EvaluateFunctionAsync("e => e.click()").ConfigureAwait(false);
    }
    private static async Task WaitForAsync(Func<Task<bool>> predicate, TimeSpan timeout, CancellationToken token)
    {
        var end = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < end) { token.ThrowIfCancellationRequested(); if (await predicate().ConfigureAwait(false)) return; await Task.Delay(250, token).ConfigureAwait(false); }
        throw new TimeoutException("TAG 页面未在时限内完成操作；原会话和有效文件已保留。");
    }
    private static async Task<byte[]> DownloadInBrowserAsync(IPage page, Uri resource, CancellationToken token)
    {
        // A top-level browser download does not depend on cross-origin fetch/CORS permission.
        // Only downloads use an ephemeral context; account cookies remain in the durable normal profile.
        string directory = Path.Combine(Path.GetTempPath(), "egress-download-" + Guid.NewGuid().ToString("N"));
        PrivateFile.EnsureDirectory(directory);
        ICDPSession? downloadSession = null;
        try
        {
            await using var context = await page.Browser.CreateBrowserContextAsync(new BrowserContextOptions()).ConfigureAwait(false);
            // 25.15 omits BrowserSetDownloadBehaviorRequest from its generated context. Send a pre-serialized
            // JsonElement instead of that internal DTO, so this path remains NativeAOT-safe without reflection.
            using var behavior = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(
                new BrowserDownloadSettings("allowAndName", directory, context.Id), BrowserDownloadJson.Default.BrowserDownloadSettings));
            downloadSession = await page.Browser.CreateCDPSessionAsync().ConfigureAwait(false);
            // Chrome scopes this override to the CDP session: keep it attached until the download completes.
            await downloadSession.SendAsync("Browser.setDownloadBehavior", behavior.RootElement).ConfigureAwait(false);
            await using var download = await context.NewPageAsync().ConfigureAwait(false);
            IResponse? response = null;
            try { response = await download.GoToAsync(resource.AbsoluteUri, new NavigationOptions { WaitUntil = [WaitUntilNavigation.DOMContentLoaded], Timeout = 30000 }).WaitAsync(token).ConfigureAwait(false); }
            catch (NavigationException) { /* Content-Disposition downloads intentionally abort document navigation. */ }
            if (response is { Ok: true })
            {
                if (response.Headers.TryGetValue("content-length", out var length) && long.TryParse(length, out long size) && size > 4 * 1024 * 1024)
                    throw new InvalidDataException("订阅文件过大。");
                byte[] body = await response.BufferAsync().AsTask().WaitAsync(token).ConfigureAwait(false);
                if (body.Length is 0 or > 4 * 1024 * 1024) throw new InvalidDataException("订阅大小无效。");
                return body;
            }
            byte[]? content = null;
            await WaitForAsync(async () =>
            {
                var files = Directory.GetFiles(directory);
                if (files.Any(f => new FileInfo(f).Length > 4 * 1024 * 1024)) throw new InvalidDataException("订阅文件过大。");
                string? completed = files.SingleOrDefault(f => !f.EndsWith(".crdownload", StringComparison.Ordinal));
                if (completed is null) return false;
                content = await File.ReadAllBytesAsync(completed, token).ConfigureAwait(false); return true;
            }, TimeSpan.FromSeconds(40), token).ConfigureAwait(false);
            return content ?? throw new InvalidDataException("浏览器未提供订阅文件。");
        }
        finally
        {
            if (downloadSession is not null) { try { await downloadSession.DetachAsync().ConfigureAwait(false); } catch (PuppeteerException) { } }
            try { Directory.Delete(directory, true); } catch (IOException) { }
        }
    }
}

internal sealed record BrowserDownloadSettings(
    [property: System.Text.Json.Serialization.JsonPropertyName("behavior")] string Behavior,
    [property: System.Text.Json.Serialization.JsonPropertyName("downloadPath")] string DownloadPath,
    [property: System.Text.Json.Serialization.JsonPropertyName("browserContextId")] string? BrowserContextId);
[System.Text.Json.Serialization.JsonSerializable(typeof(BrowserDownloadSettings))]
internal sealed partial class BrowserDownloadJson : System.Text.Json.Serialization.JsonSerializerContext;
