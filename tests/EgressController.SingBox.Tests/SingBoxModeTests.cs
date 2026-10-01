using System.Net;
using System.Text;
using System.Text.Json;
using EgressController.SingBox.Api;
using EgressController.SingBox.Configuration;

namespace EgressController.SingBox.Tests;

public sealed class SingBoxModeTests
{
    [Fact]
    public async Task Health_changes_only_patch_and_verify_mode_without_reloading_the_core()
    {
        var handler = new ModeHandler();
        using var http = new HttpClient(handler);
        using var api = new SingBoxApiClient(new Uri("http://127.0.0.1:19090"), "mock-secret", http);
        foreach (string mode in new[] { EgressDohConfiguration.CloudflareMode, EgressDohConfiguration.DnsPodMode,
            EgressDohConfiguration.ProtectedMode, EgressDohConfiguration.CloudflareMode })
        {
            await api.SetModeAsync(mode, TestContext.Current.CancellationToken);
            Assert.Equal(mode, handler.Mode);
        }
        Assert.Equal(Enumerable.Range(0, 4).SelectMany(_ => new[] { "PATCH /configs", "GET /configs" }), handler.Requests);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Ignored_mode_or_authentication_failure_cannot_report_success(bool ignore, bool unauthorized)
    {
        var handler = new ModeHandler { Ignore = ignore, Unauthorized = unauthorized };
        using var http = new HttpClient(handler);
        using var api = new SingBoxApiClient(new Uri("http://127.0.0.1:19090"), "mock-secret", http);
        await Assert.ThrowsAsync<SingBoxApiException>(() => api.SetModeAsync(EgressDohConfiguration.CloudflareMode, TestContext.Current.CancellationToken));
        Assert.Equal(EgressDohConfiguration.ProtectedMode, handler.Mode);
    }

    [Fact]
    public async Task Confirmed_unchanged_mode_skips_patch_but_core_restart_requires_reconfirmation()
    {
        var modes = new DohModeController();
        int calls = 0;
        Task Apply(string _, CancellationToken token) { calls++; return Task.CompletedTask; }
        await modes.ApplyAsync(new(), Apply, TestContext.Current.CancellationToken);
        await modes.ApplyAsync(new(), Apply, TestContext.Current.CancellationToken);
        Assert.Equal(1, calls);
        modes.Invalidate();
        await modes.ApplyAsync(new(), Apply, TestContext.Current.CancellationToken);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Failed_patch_and_invalidated_inflight_patch_cannot_populate_confirmation_cache()
    {
        var modes = new DohModeController();
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var patch = modes.ApplyAsync(new(), (_, _) => pending.Task, TestContext.Current.CancellationToken);
        modes.Invalidate();
        pending.SetResult();
        await patch;
        int calls = 0;
        await Assert.ThrowsAsync<IOException>(() => modes.ApplyAsync(new(), (_, _) =>
        {
            calls++;
            throw new IOException("mock read-back failure");
        }, TestContext.Current.CancellationToken));
        await modes.ApplyAsync(new(), (_, _) => { calls++; return Task.CompletedTask; }, TestContext.Current.CancellationToken);
        Assert.Equal(2, calls);
    }

    private sealed class ModeHandler : HttpMessageHandler
    {
        public bool Ignore, Unauthorized;
        public string Mode = EgressDohConfiguration.ProtectedMode;
        public List<string> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.Equal("Bearer mock-secret", request.Headers.Authorization!.ToString());
            Assert.Equal("/configs", request.RequestUri!.AbsolutePath);
            Requests.Add($"{request.Method} {request.RequestUri.AbsolutePath}");
            if (Unauthorized) return new(HttpStatusCode.Unauthorized);
            if (request.Method == HttpMethod.Patch)
            {
                Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
                using var body = JsonDocument.Parse(await request.Content.ReadAsStringAsync(token));
                Assert.Single(body.RootElement.EnumerateObject());
                if (!Ignore) Mode = body.RootElement.GetProperty("mode").GetString()!;
                return new(HttpStatusCode.NoContent);
            }
            Assert.Equal(HttpMethod.Get, request.Method);
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"mode\":\"" + Mode + "\"}", Encoding.UTF8, "application/json") };
        }
    }
}
