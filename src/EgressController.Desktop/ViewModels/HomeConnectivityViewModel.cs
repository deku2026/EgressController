using EgressController.Runtime;

namespace EgressController.Desktop.ViewModels;

public sealed partial class ShellViewModel
{
    private string? _checkedHomeKey;
    private DateTimeOffset _homeCheckedAt;
    private string _homeCheckStatus = "先检测家宽直连，成功后才能继续。";
    private bool _homeCheckFailed;
    private string HomeCheckKey => Document.Interfaces.HomeId + "\n" + Document.Dns.HttpsUrl;
    public bool HomeCheckReady => _checkedHomeKey == HomeCheckKey && _backend.Now - _homeCheckedAt < TimeSpan.FromMinutes(2);
    public string HomeCheckStatus => _homeCheckStatus;
    public bool HomeCheckFailed => _homeCheckFailed;
    public AsyncUiCommand CheckHomeCommand { get; private set; } = null!;
    private void InitializeHomeCheck() => CheckHomeCommand = new(() => RunOperationAsync(CheckHomeAsync), () => CanEdit && SelectedHomeInterface is not null);
    private void InvalidateHomeCheck(bool reset = false)
    {
        if (reset || _checkedHomeKey != HomeCheckKey)
        { _checkedHomeKey = null; _homeCheckFailed = false; _homeCheckStatus = "仅检测家宽 → Cloudflare / Google 204，不使用代理或非家宽。"; }
        PublishHomeCheck(); CheckHomeCommand?.Refresh();
    }
    private async Task CheckHomeAsync()
    {
        var snapshot = CopyDocument(Document); string key = HomeCheckKey;
        _checkedHomeKey = null; _homeCheckFailed = false; _homeCheckStatus = "正在通过家宽检测 DNS、TLS 和 HTTP 204…";
        PublishHomeCheck();
        try
        {
            var result = await _backend.CheckHomeConnectivityAsync(snapshot, ActionToken);
            if (key != HomeCheckKey) return;
            if (result.Available && result.InterfaceId == Document.Interfaces.HomeId)
            { _checkedHomeKey = key; _homeCheckedAt = _backend.Now; _homeCheckStatus = $"家宽直连成功 · {result.Milliseconds:F0} ms · {(result.Endpoint == "cloudflare-204" ? "Cloudflare" : "Google")} 204"; }
            else { _homeCheckFailed = true; _homeCheckStatus = "家宽检测未通过：" + NetworkFailure.Label(result.FailureCode) + "。请检查所选网络后重试。"; }
        }
        catch (Exception error) when (IsExpectedFailure(error))
        {
            RecordFailure("home.check", error);
            if (key != HomeCheckKey) return;
            _homeCheckFailed = error is not OperationCanceledException;
            _homeCheckStatus = error is OperationCanceledException ? "家宽检测已取消，请重新检测。" : "家宽检测失败：" + NetworkFailure.Label(NetworkFailure.Code(error));
        }
        finally { PublishHomeCheck(); }
    }
    private void PublishHomeCheck()
    { Raise(nameof(HomeCheckStatus)); Raise(nameof(HomeCheckReady)); Raise(nameof(HomeCheckFailed)); NextSetupCommand?.Refresh(); }
}
