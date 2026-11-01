using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EgressController.Desktop.Preview;
using EgressController.Desktop.Services;
using EgressController.Desktop.ViewModels;
using EgressController.Desktop.Views;
using EgressController.Desktop.Presentation.Controls;
using EgressController.Policy;
using EgressController.Runtime;

namespace EgressController.MacPreview;

/// <summary>Production presentation with injected fixtures; no production adapter, native query or real user file is constructed.</summary>
internal static class ProductionCompositionChecks
{
    public static void Run(string directory)
    {
        PreviewEnvironment.RequireIsolated();
        var emptyBackend=new InjectedBackend(false);var empty=new ShellViewModel(emptyBackend);Complete(empty.InitializeAsync());
        Check(empty.IsProduction&&!empty.IsPreview&&empty.Document.Applications.Count==0&&empty.Document.Nodes.Count==0&&empty.Observations.Count==0&&empty.OnboardingVisible,"Production initial composition leaked fixture configuration or chart observations.");
        var first=new MainWindow{DataContext=empty,Width=1260,Height=860};first.Show();Save(first,Path.Combine(directory,"production-first-run-normal.png"));
        first.Width=980;first.Height=700;Save(first,Path.Combine(directory,"production-first-run-compact.png"));
        AssertNoPreviewText(first);
        empty.SelectedHomeInterface = empty.InterfaceChoices[0]; empty.SelectedNonHomeInterface = empty.InterfaceChoices[1];
        empty.SetupStep = 1; empty.SubscriptionUrl = "https://subscription.example.invalid/first-use";
        emptyBackend.SubscriptionFailure = new ResourcePreparationException("subscription-first", "subscription-direct:test-other", 443, PreparationStage.Downloading, false);
        Complete(empty.ImportSubscriptionCommand.ExecuteAsync());
        Check(empty.HasIssue && empty.CurrentIssue.Contains("直连", StringComparison.Ordinal) && !empty.ConnectionAttempt.IsVisible,
            "A failed direct subscription must report its own network error, without starting connection preparation.");
        Save(first, Path.Combine(directory, "subscription-direct-failed-compact.png"));
        emptyBackend.SubscriptionFailure = null; Complete(empty.ImportSubscriptionCommand.ExecuteAsync());
        Check(!empty.HasIssue && empty.Document.Nodes.Count == 1 && emptyBackend.SubscriptionCalls == 2 && emptyBackend.RuntimeMutationCalls == 0
            && !emptyBackend.Runtime.HelperInstalled && !empty.PreviewRunning, "First subscription import depended on a helper, TUN, or local YAML, or retry retained its old failure.");
        Check(empty.SubscriptionDescription.Contains("直连", StringComparison.Ordinal), "Subscription instructions did not explain direct access.");
        Save(first, Path.Combine(directory, "subscription-direct-ready-compact.png"));
        first.Width = 1260; first.Height = 860; Save(first, Path.Combine(directory, "subscription-direct-ready-normal.png"));
        AssertNoPreviewText(first);first.Close();Complete(empty.DisposeAsync().AsTask());

        var loadedBackend=new InjectedBackend(true);var vm=new ShellViewModel(loadedBackend);Complete(vm.InitializeAsync());vm.PageIndex=1;
        var window=new MainWindow{DataContext=vm,Width=1260,Height=860};window.Show();Flush();
        var iconDeadline = DateTime.UtcNow.AddSeconds(5);
        while (vm.VisibleApps.Any(row => row.Icon is null) && DateTime.UtcNow < iconDeadline) { Flush(); Thread.Sleep(1); }
        Flush();
        Check(loadedBackend.IconRequests>0&&vm.VisibleApps.All(row=>row.Icon is not null),"Production application artwork did not load through injected native-PNG service.");
        Check(window.GetVisualDescendants().OfType<ProductAvatar>().Any(avatar=>avatar.IsEffectivelyVisible&&avatar.Source is not null),"Native PNG artwork was not bound to the shared backing control.");
        Save(window,Path.Combine(directory,"production-apps-native-png.png"));AssertNoPreviewText(window);
        vm.PageIndex = 3; loadedBackend.ProbePause = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowProbe = vm.RunProbesAsync(); Flush();
        Check(vm.IsProbing && !vm.IsBusy && vm.CanEdit, "A slow probe disabled navigation or editing.");
        var appsNav = window.FindControl<Button>("AppsNav")!;
        var navPoint = appsNav.TranslatePoint(new Point(appsNav.Bounds.Width / 2, appsNav.Bounds.Height / 2), window)!.Value;
        window.MouseDown(navPoint, Avalonia.Input.MouseButton.Left); window.MouseUp(navPoint, Avalonia.Input.MouseButton.Left); Flush();
        Check(vm.AppsVisible, "Pointer navigation was blocked while probing.");
        vm.PageIndex = 3; Save(window, Path.Combine(directory, "probe-running-interactive.png"));
        vm.CancelProbeCommand.Execute(null); Complete(slowProbe);
        Check(!vm.IsProbing && vm.CanProbe, "Cancel left probing controls busy."); loadedBackend.ProbePause = null;
        vm.PageIndex=0;Save(window,Path.Combine(directory,"production-no-observations.png"));AssertNoPreviewText(window);

        foreach(Exception failure in new Exception[]{new InvalidDataException("credential-marker parser excerpt"),new IOException("https://credential-marker.example.invalid/?token=private")})
        {
            loadedBackend.PreparationFailure=failure;Complete(vm.CompileCommand.ExecuteAsync());
            Check(vm.HasIssue&&!vm.CurrentIssue.Contains("credential-marker",StringComparison.Ordinal)&&vm.Events.All(entry=>!entry.Detail.Contains("credential-marker",StringComparison.Ordinal)),"Expected resource-validation exception crashed the action or leaked endpoint credentials into UI diagnostics.");
        }
        loadedBackend.PreparationFailure=null;
        loadedBackend.StatusFailure=new IOException("credential-marker stale reply");Complete(vm.RefreshStatusAsync());Check(!vm.PreviewRunning,"A failed status read retained a healthy running state.");
        loadedBackend.StatusFailure=null;loadedBackend.Runtime=new(false,false,true,"后台服务当前不可用，请检查日志后重试。");Complete(vm.RefreshStatusAsync());
        Save(window,Path.Combine(directory,"production-unavailable.png"));AssertNoPreviewText(window);
        loadedBackend.Runtime=new(true,true,true,"已确认后台服务与 TUN 接管。");Complete(vm.RefreshStatusAsync());Check(vm.PreviewRunning,"A fresh confirmed status did not recover the production state.");
        window.Close();Complete(vm.DisposeAsync().AsTask());

        var offlineBackend = new InjectedBackend(true) { Offline = true, AllowApply = true, ResourcePause = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var offline = new ShellViewModel(offlineBackend); Complete(offline.InitializeAsync()); offline.OnboardingVisible = true; offline.SetupStep = 4;
        var offlineWindow = new MainWindow { DataContext = offline, Width = 1260, Height = 860 }; offlineWindow.Show();
        var connecting = offline.ApplyCommand.ExecuteAsync(); Flush();
        Check(offlineBackend.RuntimeMutationCalls == 1 && offline.ConnectionAttempt.Steps[3].State == "完成"
            && offline.ConnectionAttempt.Steps[4].State == "进行中", "Offline interface flags prevented TUN before resource preparation.");
        Save(offlineWindow, Path.Combine(directory, "tun-offline-started-normal.png"));
        offlineWindow.Width = 980; offlineWindow.Height = 700;
        Save(offlineWindow, Path.Combine(directory, "tun-offline-started-compact.png"));
        offlineBackend.ResourcePause.SetException(new ResourcePreparationException("rules-cn", "xray-socks5:127.0.0.1:17890", 443, PreparationStage.Downloading, false));
        Complete(connecting);
        Check(offline.ConnectionAttempt.Steps[3].State == "完成" && offline.ConnectionAttempt.Steps[4].State == "需处理"
            && offline.ConnectionAttempt.Detail.Contains("非家宽", StringComparison.Ordinal), "Outbound resource failure was incorrectly attributed to TUN creation.");
        Save(offlineWindow, Path.Combine(directory, "tun-offline-resource-failure-compact.png"));
        offlineBackend.ResourcePause = null; Complete(offline.ApplyCommand.ExecuteAsync());
        Check(offline.PreviewRunning && !offline.ConnectionAttempt.HasFailed, "Retry with cached resources was gated on offline physical links.");
        offlineWindow.Close(); Complete(offline.DisposeAsync().AsTask());
    }
    private static void AssertNoPreviewText(Window window)
    {
        var leaked=window.GetVisualDescendants().OfType<TextBlock>().Where(text=>text.IsEffectivelyVisible).Select(text=>text.Text??"").FirstOrDefault(text=>text.Contains("示例",StringComparison.Ordinal)||text.Contains("模拟",StringComparison.Ordinal)||text.Contains("PREVIEW",StringComparison.Ordinal)||text.Contains("MOCK",StringComparison.Ordinal));
        Check(leaked is null,"Production UI retained preview-only text: "+leaked);
    }
    private static void Complete(Task task)
    {
        var deadline=DateTime.UtcNow.AddSeconds(20);
        while(!task.IsCompleted){Dispatcher.UIThread.RunJobs();if(DateTime.UtcNow>deadline)throw new TimeoutException("Injected UI task did not finish.");Thread.Sleep(1);}
        task.GetAwaiter().GetResult();Flush();
    }
    private static void Flush(){Dispatcher.UIThread.RunJobs();AvaloniaHeadlessPlatform.ForceRenderTimerTick();Dispatcher.UIThread.RunJobs();}
    private static void Save(Window window,string path){Flush();using var frame=window.CaptureRenderedFrame()??throw new InvalidOperationException("No production-composition frame.");frame.Save(path,new Avalonia.Media.Imaging.PngBitmapEncoderOptions());}
    private static void Check(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}

    private sealed class InjectedBackend(bool populated):IClientBackend
    {
        private readonly FixtureClientBackend _fixtures=new();
        private MacPolicyDocument _document=Document(populated);
        public bool IsPreview=>false;
        public DateTimeOffset Now=>new(2026,10,30,12,0,0,TimeSpan.Zero);
        public string LogDirectory=>PreviewEnvironment.SessionDirectory;
        public ClientRuntimeState Runtime{get;set;}=new(false,true,populated,populated?"已停止接管，保存的策略继续保留。":"启动时请求必要的网络授权。");
        public Exception? PreparationFailure{get;set;}
        public Exception? StatusFailure{get;set;}
        public int IconRequests{get;private set;}
        public Exception? SubscriptionFailure { get; set; }
        public int SubscriptionCalls { get; private set; }
        public int RuntimeMutationCalls { get; private set; }
        public bool Offline { get; init; }
        public bool AllowApply { get; init; }
        public TaskCompletionSource<IReadOnlyList<GroupProbe>>? ProbePause { get; set; }
        public TaskCompletionSource<ClientPreparedResources>? ResourcePause { get; set; }
        private IProgress<ClientOperationProgress>? _progress;
        public void SetProgressSink(IProgress<ClientOperationProgress>? progress) => _progress = progress;
        public Task<ClientBootstrap> LoadAsync(CancellationToken cancellationToken=default)=>Task.FromResult(new ClientBootstrap(_document,[new("test-home","en5","Studio Ethernet",Offline ? "未连接" : "有线接口",!Offline),new("test-other","en0","Travel Wi-Fi",Offline ? "未连接" : "无线接口",!Offline)],Applications(),populated,Runtime));
        private static MacPolicyDocument Document(bool populated)
        {
            var document=new MacPolicyDocument();if(!populated)return document;
            document.Interfaces.HomeId="test-home";document.Interfaces.NonHomeId="test-other";
            document.Nodes.Add(new(){Id="test-proxy",Name="Workspace Node",Protocol=NodeProtocol.Vless,Server="node.example.invalid",Port=443,UserId="11111111-1111-4111-8111-111111111111",Tls=true});document.Groups.Add(new() { Id = "test-global", Name = "Global", Kind = GroupKind.Select, MemberIds = ["test-proxy"], SelectedMemberId = "test-proxy" });document.Defaults.Global=ExitChoice.Proxy("test-global");
            document.Applications.Add(new(){Id="test-editor",Name="Sample Editor",BundlePath="/Applications/Sample Editor.app",PrimaryExecutable="/Applications/Sample Editor.app/Contents/MacOS/Sample Editor",ExecutablePaths=["/Applications/Sample Editor.app/Contents/MacOS/Sample Editor"],Via=ExitChoice.NonHome});return document;
        }
        private static IReadOnlyList<ClientApplication> Applications()=>[new("test-editor","Sample Editor","vscode","开发工具","/Applications/Sample Editor.app","/Applications/Sample Editor.app/Contents/MacOS/Sample Editor",[],false)];
        public Task<IReadOnlyList<ClientApplication>> DiscoverApplicationsAsync(CancellationToken cancellationToken=default)=>Task.FromResult(Applications());
        public Task<ClientApplication> BuildInventoryAsync(ClientApplication app,IReadOnlyList<string> saved,CancellationToken cancellationToken=default)=>Task.FromResult(app with{ExecutablePaths=saved.Concat([app.PrimaryExecutable!]).Distinct(StringComparer.Ordinal).ToArray(),InventoryComplete=true});
        public Task<byte[]?> LoadIconAsync(string bundlePath,CancellationToken cancellationToken=default)
        {
            IconRequests++;using var stream=AssetLoader.Open(new Uri("avares://EgressController.Desktop.Presentation/Assets/vscode.png"));using var memory=new MemoryStream();stream.CopyTo(memory);return Task.FromResult<byte[]?>(memory.ToArray());
        }
        public Task<IReadOnlyList<ClientRuleCatalogEntry>> LoadRuleCatalogAsync(MacPolicyDocument document,CancellationToken cancellationToken=default)=>_fixtures.LoadRuleCatalogAsync(document,cancellationToken);
        public Task<MacPolicyDocument> ImportSubscriptionAsync(Uri uri,MacPolicyDocument current,CancellationToken cancellationToken=default)
        {
            SubscriptionCalls++; if (SubscriptionFailure is { } failure) return Task.FromException<MacPolicyDocument>(failure);
            const string yaml = "proxies: [{name: Direct node, type: socks5, server: node.example.invalid, port: 1080}]\nproxy-groups: [{name: Global, type: select, proxies: [Direct node]}]";
            return Task.FromResult(SubscriptionReconciler.Merge(current, "direct-first", ClashSubscriptionImporter.Import(yaml, "direct-first")));
        }
        public Task<ClientPreparedResources> PrepareResourcesAsync(MacPolicyDocument document,CancellationToken cancellationToken=default)=> ResourcePause?.Task ?? (PreparationFailure is {} failure?Task.FromException<ClientPreparedResources>(failure):_fixtures.PrepareResourcesAsync(document,cancellationToken));
        public Task<IReadOnlyList<GroupProbe>> ProbeAsync(MacPolicyDocument document,InterfaceBinding binding,CancellationToken cancellationToken=default)=>ProbePause?.Task.WaitAsync(cancellationToken) ?? Task.FromResult<IReadOnlyList<GroupProbe>>([]);
        public Task BeginConnectionAsync(MacPolicyDocument document, XrayCompileOptions options, CancellationToken cancellationToken = default) {
            RuntimeMutationCalls++;
            if (Offline) Check(options.Interfaces.Count == 2, "Offline interface identities were removed before TUN compilation.");
            var bootstrap = XrayConfigCompiler.Compile(document, new() { Interfaces = options.Interfaces, ResourceBootstrap = true });
            Check(bootstrap.IsSuccess, "Valid offline interfaces prevented bootstrap compilation.");
            _progress?.Report(new(ConnectionAttemptStage.Bootstrap, "正在建立 TUN；物理出口在线状态单独处理。"));
            return Task.CompletedTask;
        }
        public Task<ApplyResult> ApplyAsync(MacPolicyDocument document,XrayCompileOptions options,IReadOnlyDictionary<string,byte[]> assets,CancellationToken cancellationToken=default){
            if (!AllowApply) return Task.FromResult(new ApplyResult(ApplyDisposition.Rejected,null,[new("test.only","This injected composition never starts a network core.")]));
            Runtime = new(true, true, true, "后台服务已确认接管。");
            return Task.FromResult(new ApplyResult(ApplyDisposition.Applied,"synthetic-offline",[]));
        }
        public Task<bool> StopAsync(CancellationToken cancellationToken=default){RuntimeMutationCalls++;Runtime=Runtime with{Running=false};return Task.FromResult(true);}
        public Task<ClientRuntimeState> ReadStatusAsync(CancellationToken cancellationToken=default)=>StatusFailure is {} failure?Task.FromException<ClientRuntimeState>(failure):Task.FromResult(Runtime);
        public Task SaveDraftAsync(MacPolicyDocument document,CancellationToken cancellationToken=default){_document=document;return Task.CompletedTask;}
        public Task OpenLogDirectoryAsync(CancellationToken cancellationToken=default)=>Task.CompletedTask;
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
}
