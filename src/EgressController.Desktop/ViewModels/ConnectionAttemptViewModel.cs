using EgressController.Desktop.Services;

namespace EgressController.Desktop.ViewModels;

public sealed class ConnectionAttemptStep(string title, int number) : Bindable
{
    private string _state = "等待";
    public string Title { get; } = title;
    public string Number { get; } = number.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public string State { get => _state; private set { if (Set(ref _state, value)) Raise(nameof(Color)); } }
    public string Color => State switch { "完成" => "#368571", "进行中" => "#4778D9", "需处理" => "#B45C67", _ => "#98A4B7" };
    public void Update(string state) => State = state;
}

/// <summary>Presentation of observed preparation phases, separate from network health.</summary>
public sealed class ConnectionAttemptViewModel(Action retry) : Bindable
{
    private bool _visible, _running, _failed;
    private int _stage;
    private string _detail = "", _title = "准备连接";
    public IReadOnlyList<ConnectionAttemptStep> Steps { get; } = new[] { "检查策略", "准备资源", "验证内核", "授权助手", "确认连接" }
        .Select((title, index) => new ConnectionAttemptStep(title, index + 1)).ToArray();
    public UiCommand RetryCommand { get; } = new(retry);
    public bool IsVisible { get => _visible; private set => Set(ref _visible, value); }
    public bool IsRunning { get => _running; private set => Set(ref _running, value); }
    public bool HasFailed { get => _failed; private set => Set(ref _failed, value); }
    public string Title { get => _title; private set => Set(ref _title, value); }
    public string Detail { get => _detail; private set => Set(ref _detail, value); }
    public void Begin()
    {
        IsVisible = true; IsRunning = true; HasFailed = false; _stage = 0;
        foreach (var step in Steps) step.Update("等待");
        Observe(new(ConnectionAttemptStage.Validating, "检查保存的策略、物理出口和全局代理分组。"));
    }
    public void Observe(ClientOperationProgress progress)
    {
        if (!IsRunning || progress.Stage == ConnectionAttemptStage.Ready) return;
        int next = (int)progress.Stage;
        if (next < _stage || next >= Steps.Count) return;
        _stage = next;
        for (int index = 0; index < Steps.Count; index++) Steps[index].Update(index < next ? "完成" : index == next ? "进行中" : "等待");
        Title = Steps[next].Title; Detail = progress.Detail;
    }
    public void Complete(bool preview = false)
    {
        IsRunning = false; HasFailed = false; Title = preview ? "模拟连接已完成" : "连接已确认"; Detail = preview ? "隔离内存适配器已完成流程；未启动真实 TUN。" : "后台服务已确认 Xray 与实际 TUN 接管。";
        foreach (var step in Steps) step.Update("完成");
    }
    public void Fail(string detail, bool canceled = false)
    {
        if (!IsVisible) return;
        IsRunning = false; HasFailed = true; Title = canceled ? "连接准备已取消" : "连接尚未完成";
        Detail = detail; Steps[_stage].Update("需处理");
    }
}
