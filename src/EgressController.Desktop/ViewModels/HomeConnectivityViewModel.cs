using EgressController.Runtime;

namespace EgressController.Desktop.ViewModels;

public sealed partial class ShellViewModel
{
    private string? _checkedHomeKey;
    private DateTimeOffset _homeCheckedAt;
    private string _homeCheckStatus = "先检测家宽直连，成功后才能继续。";
    private string HomeCheckKey => Document.Interfaces.HomeId + "\n" + Document.Dns.HttpsUrl;
    public bool HomeCheckReady => _checkedHomeKey == HomeCheckKey && _backend.Now - _homeCheckedAt < TimeSpan.FromMinutes(2);
    public string HomeCheckStatus => _homeCheckStatus;
    public AsyncUiCommand CheckHomeCommand { get; private set; } = null!;
    private void InitializeHomeCheck() => CheckHomeCommand = new(() => RunOperationAsync(CheckHomeAsync), () => CanEdit && SelectedHomeInterface is not null);
    private void InvalidateHomeCheck(bool reset = false)
    {
        if (reset || _checkedHomeKey != HomeCheckKey)
        { _checkedHomeKey = null; _homeCheckStatus = "仅检测家宽 → Cloudflare / Google 204，不使用代理或非家宽。"; }
        Raise(nameof(HomeCheckReady)); Raise(nameof(HomeCheckStatus)); CheckHomeCommand?.Refresh(); NextSetupCommand?.Refresh();
    }
    private async Task CheckHomeAsync()
    {
        var snapshot = CopyDocument(Document); string key = HomeCheckKey;
        _checkedHomeKey = null; _homeCheckStatus = "正在通过家宽检测 DNS、TLS 和 HTTP 204…";
        Raise(nameof(HomeCheckStatus)); Raise(nameof(HomeCheckReady));
        try
        {
            var result = await _backend.CheckHomeConnectivityAsync(snapshot, ActionToken);
            if (key != HomeCheckKey) return;
            if (result.Available && result.InterfaceId == Document.Interfaces.HomeId)
            { _checkedHomeKey = key; _homeCheckedAt = _backend.Now; _homeCheckStatus = $"家宽直连成功 · {result.Milliseconds:F0} ms · {(result.Endpoint == "cloudflare-204" ? "Cloudflare" : "Google")} 204"; }
            else _homeCheckStatus = "家宽检测未通过：" + NetworkFailure.Label(result.FailureCode) + "。请检查 iPhone 共享和网络后重试。";
        }
        catch (Exception error) when (IsExpectedFailure(error))
        { RecordFailure("home.check", error); _homeCheckStatus = error is OperationCanceledException ? "家宽检测已取消，请重新检测。" : "家宽检测失败：" + NetworkFailure.Label(NetworkFailure.Code(error)); }
        finally { Raise(nameof(HomeCheckStatus)); Raise(nameof(HomeCheckReady)); NextSetupCommand.Refresh(); }
    }
}
