using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EgressController.Desktop.Preview;
using EgressController.Desktop.Services;
using EgressController.Desktop.ViewModels;
using EgressController.Desktop.Views;
using EgressController.Policy;
using EgressController.Runtime;

namespace EgressController.MacPreview;

/// <summary>Pure phase transitions and production presentation with an injected, inert backend.</summary>
internal static class ConnectionAttemptChecks
{
    public static void Run(string directory)
    {
        PreviewEnvironment.RequireIsolated();
        PureTransitionsAndRetry(directory);
        InstallerFailureVisibility(directory);
        var backend = new InertBackend(); var shell = new ShellViewModel(backend); Complete(shell.InitializeAsync());
        string policy = PolicyYaml.Write(shell.Document);
        Check(shell.IsProduction && !shell.HasChanges && backend.ApplyCalls == 0 && backend.PreparationCalls == 0, "Injected production startup performed connection preparation or mutated the policy.");
        shell.OnboardingVisible = true; shell.SetupStep = 4;
        Check(shell.SetupReview && !shell.SetupDescription.Contains("模拟", StringComparison.Ordinal) && !shell.SetupApplyDescription.Contains("模拟", StringComparison.Ordinal), "Production setup review retained simulated-connection wording.");
        var window = new MainWindow { DataContext = shell, Width = 1260, Height = 860 }; window.Show(); Flush();
        SaveSizes(window, directory, "attempt-production-setup-review"); AssertProductionText(window);

        shell.ConnectionAttempt.Begin();
        shell.ConnectionAttempt.Observe(new(ConnectionAttemptStage.Resources, "正在校验本地缓存与资源完整性。可取消准备，已保存配置会继续保留。"));
        SaveSizes(window, directory, "attempt-production-setup-resources"); AssertProductionText(window);
        shell.ConnectionAttempt.Begin(); shell.ConnectionAttempt.Observe(new(ConnectionAttemptStage.Bootstrap, "建立 TUN 与 Xray 本地代理。"));
        shell.ConnectionAttempt.Fail(RuntimeFailures.Message("proxy.port-in-use"));
        SaveSizes(window, directory, "attempt-port-conflict");
        shell.ConnectionAttempt.Begin(); shell.ConnectionAttempt.Observe(new(ConnectionAttemptStage.Resources, "通过内置 Xray · 非家宽分组节点 · SOCKS5 127.0.0.1:17890 下载规则。"));
        shell.OnboardingVisible = false;
        SaveSizes(window, directory, "attempt-resources");
        shell.ConnectionAttempt.Fail("资源未通过完整性校验。已验证缓存与原有配置已保留，请检查后重试。");
        SaveSizes(window, directory, "attempt-failed"); AssertProductionText(window);
        Check(window.GetVisualDescendants().OfType<Button>().Any(button => button.IsEffectivelyVisible && button.Content as string == "重试连接"), "Failed connection did not expose its retry action.");
        shell.ConnectionAttempt.Begin();
        shell.ConnectionAttempt.Observe(new(ConnectionAttemptStage.Resources, "已重新开始检查缓存与资源；只发布完整、通过校验的文件。"));
        SaveSizes(window, directory, "attempt-retry");
        shell.ConnectionAttempt.Fail("已取消本次准备，已保存配置和已验证缓存保持可用。", canceled: true);
        SaveSizes(window, directory, "attempt-canceled"); AssertProductionText(window);
        Check(!shell.ConnectionAttempt.IsRunning && shell.ConnectionAttempt.HasFailed && shell.ConnectionAttempt.Title.Contains("取消", StringComparison.Ordinal), "Canceled attempt did not leave an actionable terminal state.");
        Check(PolicyYaml.Write(shell.Document) == policy && !shell.HasChanges && backend.ApplyCalls == 0 && backend.PreparationCalls == 0 && backend.StopCalls == 0, "Phase presentation changed policy or called a runtime/resource operation.");
        window.Close(); Complete(shell.DisposeAsync().AsTask());
    }

    private static void InstallerFailureVisibility(string directory)
    {
        var backend = new InertBackend { FailInstallation = true };
        var shell = new ShellViewModel(backend); Complete(shell.InitializeAsync());
        shell.OnboardingVisible = true; shell.SetupStep = 4;
        var window = new MainWindow { DataContext = shell, Width = 1260, Height = 860 }; window.Show(); Flush();
        Complete(shell.ApplyCommand.ExecuteAsync());
        Check(shell.HasIssue && shell.ConnectionAttempt.HasFailed && backend.Failures.Contains("connection.prepare"), "Installation failure was hidden or not logged.");
        var error = window.GetVisualDescendants().OfType<Border>().Single(control => control.Name == "SetupErrorPanel");
        var scroll = window.GetVisualDescendants().OfType<ScrollViewer>().Single(control => control.Name == "SetupContentScroll");
        foreach (var size in new[] { (1260, 860), (980, 700) })
        {
            window.Width = size.Item1; window.Height = size.Item2; Flush();
            scroll.Offset = new Vector(0, 9999); Flush();
            var position = error.TranslatePoint(new Point(), window)!.Value;
            Check(error.IsEffectivelyVisible && position.Y >= 0 && position.Y + error.Bounds.Height < window.Height,
                "Setup error must stay visible above scroll content at both sizes.");
            Save(window, Path.Combine(directory, $"attempt-install-error-{size.Item1}.png"));
        }
        var logs = window.GetVisualDescendants().OfType<Button>().Single(control => control.Name == "SetupOpenLogDirectoryButton");
        var point = logs.TranslatePoint(new Point(logs.Bounds.Width / 2, logs.Bounds.Height / 2), window)!.Value;
        window.MouseMove(point); Save(window, Path.Combine(directory, "attempt-install-logs-hover.png"));
        logs.Focus(); Save(window, Path.Combine(directory, "attempt-install-logs-focus.png"));
        window.MouseDown(point, MouseButton.Left); Save(window, Path.Combine(directory, "attempt-install-logs-pressed.png"));
        window.MouseUp(point, MouseButton.Left); Flush(); Check(backend.OpenLogsCalls == 1, "Log directory button is obstructed.");
        var diagnostics = window.GetVisualDescendants().OfType<Button>().Single(control => control.Name == "SetupViewDiagnosticsButton");
        diagnostics.Command!.Execute(null); Flush();
        Check(!shell.OnboardingVisible && shell.SettingsVisible && shell.SettingsTab == 4, "Opening diagnostics must dismiss the overlay without discarding choices.");
        shell.OnboardingVisible = true; backend.WaitForRetry = new();
        Task retry = shell.ApplyCommand.ExecuteAsync(); Flush();
        Check(shell.IsBusy && !shell.HasIssue && !error.IsVisible, "A retry retained the previous failure banner.");
        Check(scroll.IsEffectivelyEnabled, "The progress area cannot be scrolled while authorization is pending.");
        Save(window, Path.Combine(directory, "attempt-install-retry-pending.png"));
        backend.WaitForRetry.SetResult(); Complete(retry);
        Check(shell.HasIssue && backend.ApplyCalls == 0 && backend.PreparationCalls == 0, "Injected install failure reached a real runtime/resource stage.");
        window.Close(); Complete(shell.DisposeAsync().AsTask());
    }

    private static void PureTransitionsAndRetry(string directory)
    {
        int retries = 0; ConnectionAttemptViewModel? model = null;
        model = new(() => { retries++; model!.Begin(); });
        for (int phase = 0; phase < 6; phase++)
        {
            model.Begin(); model.Observe(new((ConnectionAttemptStage)phase, "受控的阶段进度。"));
            Check(model.IsVisible && model.IsRunning && !model.HasFailed && model.Steps.Select(step => step.State).SequenceEqual(Enumerable.Range(0, 6).Select(index => index < phase ? "完成" : index == phase ? "进行中" : "等待")), "Six-phase progress did not distinguish completed/current/pending steps.");
            if (phase > 0) { string title = model.Title; model.Observe(new(ConnectionAttemptStage.Validating, "迟到的旧进度")); Check(model.Title == title, "A late earlier phase moved progress backwards."); }
            model.Fail("本阶段未通过校验，已保留此前正常文件。");
            string[] states = model.Steps.Select(step => step.State).ToArray(); string detail = model.Detail;
            Check(!model.IsRunning && model.HasFailed && states[phase] == "需处理", "A failed phase remained busy or lost the actionable failure marker.");
            model.Observe(new(ConnectionAttemptStage.Connecting, "迟到的异步回调")); model.Observe(new(ConnectionAttemptStage.Ready, "不应提前宣布成功"));
            Check(model.Detail == detail && model.Steps.Select(step => step.State).SequenceEqual(states), "Late progress changed a terminal failed attempt or announced success.");
            model.RetryCommand.Execute(null);
            Check(model.IsRunning && !model.HasFailed && model.Steps[0].State == "进行中" && model.Steps.Skip(1).All(step => step.State == "等待"), "Retry retained failed/completed steps from the previous attempt.");
            model.Fail("已取消准备。", canceled: true);
            Check(!model.IsRunning && model.Title.Contains("取消", StringComparison.Ordinal), "Cancellation did not terminate a phase.");
        }
        Check(retries == 6, "Pure retry action did not execute once per failed phase.");
        model.Begin(); model.Complete(); string completed = model.Detail;
        model.Observe(new(ConnectionAttemptStage.Resources, "迟到的资源回调"));
        Check(!model.IsRunning && !model.HasFailed && !model.Title.Contains("模拟", StringComparison.Ordinal) && model.Detail == completed && model.Steps.All(step => step.State == "完成"), "Production completion leaked preview wording or accepted stale progress.");
        model.Begin(); model.Complete(preview: true); Check(model.Title.Contains("模拟", StringComparison.Ordinal), "Explicit preview completion did not identify isolation.");

        model.Begin(); model.Observe(new(ConnectionAttemptStage.Resources, "正在检查合成缓存。")); model.Fail("合成资源校验未通过；未执行网络或系统操作。");
        var window = new Window { Content = new ConnectionAttemptView { DataContext = model }, Width = 620, Height = 240, Padding = new Thickness(24) }; window.Show(); Flush();
        var retry = window.GetVisualDescendants().OfType<Button>().Single(button => button.IsEffectivelyVisible && button.Content as string == "重试连接");
        var point = retry.TranslatePoint(new Point(retry.Bounds.Width / 2, retry.Bounds.Height / 2), window) ?? throw new InvalidOperationException("Missing retry control coordinates.");
        window.MouseMove(point); Save(window, Path.Combine(directory, "attempt-retry-control-hover.png"));
        window.MouseDown(point, MouseButton.Left); Save(window, Path.Combine(directory, "attempt-retry-control-pressed.png")); window.MouseUp(point, MouseButton.Left); Flush();
        Check(retries == 7 && model.IsRunning && !model.HasFailed, "Compiled retry binding did not receive real pointer input.");
        window.Close();
    }
    private static void AssertProductionText(Window window)
    {
        string[] forbidden = ["模拟", "PREVIEW", "MOCK"];
        var leaked = window.GetVisualDescendants().OfType<TextBlock>().Where(text => text.IsEffectivelyVisible).Select(text => text.Text ?? "").FirstOrDefault(text => forbidden.Any(word => text.Contains(word, StringComparison.Ordinal)));
        Check(leaked is null, "Production review/progress retained preview text: " + leaked);
    }
    private static void SaveSizes(Window window, string directory, string name)
    {
        window.Width = 1260; window.Height = 860; Save(window, Path.Combine(directory, name + "-normal.png"));
        window.Width = 980; window.Height = 700; Save(window, Path.Combine(directory, name + "-compact.png"));
    }
    private static void Complete(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!task.IsCompleted) { Dispatcher.UIThread.RunJobs(); if (DateTime.UtcNow > deadline) throw new TimeoutException("Inert attempt UI task did not complete."); Thread.Sleep(1); }
        task.GetAwaiter().GetResult(); Flush();
    }
    private static void Flush() { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    private static void Save(Window window, string path) { Flush(); using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No connection-attempt frame."); frame.Save(path, new PngBitmapEncoderOptions()); }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private sealed class InertBackend : IClientBackend
    {
        public bool IsPreview => false;
        public DateTimeOffset Now => new(2026, 10, 31, 14, 0, 0, TimeSpan.Zero);
        public string LogDirectory => PreviewEnvironment.SessionDirectory;
        public bool FailInstallation;
        public TaskCompletionSource? WaitForRetry;
        public int OpenLogsCalls;
        public List<string> Failures { get; } = [];
        private IProgress<ClientOperationProgress>? _progress;
        public void SetProgressSink(IProgress<ClientOperationProgress>? progress) => _progress = progress;
        public void RecordFailure(string operation, Exception error) => Failures.Add(operation);
        public async Task BeginConnectionAsync(MacPolicyDocument document, XrayCompileOptions options, CancellationToken cancellationToken = default)
        {
            _progress?.Report(new(ConnectionAttemptStage.Authorization, "请求管理员授权安装网络助手。"));
            if (WaitForRetry is not null) await WaitForRetry.Task;
            if (FailInstallation) throw new RuntimeFailureException("helper.install-data");
        }
        public int ApplyCalls { get; private set; }
        public int PreparationCalls { get; private set; }
        public int StopCalls { get; private set; }
        public Task<ClientBootstrap> LoadAsync(CancellationToken cancellationToken = default)
        {
            var document = new MacPolicyDocument(); document.Interfaces.HomeId = "test-home"; document.Interfaces.NonHomeId = "test-other";
            document.Nodes.Add(new() { Id = "test-node", Name = "Workspace Node", Protocol = NodeProtocol.Vless, Server = "node.example.invalid", Port = 443, UserId = "11111111-1111-4111-8111-111111111111", Tls = true });
            document.Groups.Add(new() { Id = "test-group", Name = "Workspace Group", Kind = GroupKind.Select, MemberIds = ["test-node"], SelectedMemberId = "test-node" }); document.Defaults.Global = ExitChoice.Proxy("test-group");
            return Task.FromResult(new ClientBootstrap(document, [new("test-home", "en5", "Home Ethernet", "有线接口", true), new("test-other", "en0", "Travel Wi-Fi", "无线接口", true)], [], true, new(false, true, false, "尚未连接；等待用户明确启动。")));
        }
        public Task<IReadOnlyList<ClientApplication>> DiscoverApplicationsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ClientApplication>>([]);
        public Task<ClientApplication> BuildInventoryAsync(ClientApplication application, IReadOnlyList<string> savedPaths, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Inert attempt checks never scan an application.");
        public Task<byte[]?> LoadIconAsync(string bundlePath, CancellationToken cancellationToken = default) => Task.FromResult<byte[]?>(null);
        public Task<IReadOnlyList<ClientRuleCatalogEntry>> LoadRuleCatalogAsync(MacPolicyDocument document, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ClientRuleCatalogEntry>>([]);
        public Task<MacPolicyDocument> ImportSubscriptionAsync(Uri url, MacPolicyDocument current, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Inert attempt checks never fetch a subscription.");
        public Task<ClientPreparedResources> PrepareResourcesAsync(MacPolicyDocument document, CancellationToken cancellationToken = default) { PreparationCalls++; throw new InvalidOperationException("Inert attempt checks never prepare a resource."); }
        public Task<IReadOnlyList<GroupProbe>> ProbeAsync(MacPolicyDocument document, InterfaceBinding binding, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Inert attempt checks never probe a node.");
        public Task<ApplyResult> ApplyAsync(MacPolicyDocument document, XrayCompileOptions options, IReadOnlyDictionary<string, byte[]> assets, CancellationToken cancellationToken = default) { ApplyCalls++; throw new InvalidOperationException("Inert attempt checks never apply a runtime."); }
        public Task<bool> StopAsync(CancellationToken cancellationToken = default) { StopCalls++; throw new InvalidOperationException("Inert attempt checks never stop a runtime."); }
        public Task<ClientRuntimeState> ReadStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(new ClientRuntimeState(false, true, false, "尚未连接。"));
        public Task SaveDraftAsync(MacPolicyDocument document, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task OpenLogDirectoryAsync(CancellationToken cancellationToken = default) { OpenLogsCalls++; return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
