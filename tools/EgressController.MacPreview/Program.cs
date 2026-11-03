using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using EgressController.Core.Protection;
using EgressController.MacApp;
using EgressController.MacApp.Preview;
using EgressController.MacApp.ViewModels;
using EgressController.Presentation;
using EgressController.SubscriptionUI;

RuntimeSafety.EnableMockOnly();
Environment.SetEnvironmentVariable("EGRESS_MOCK_ONLY", "1");
string output = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.GetFullPath("artifacts/mac-preview");
Directory.CreateDirectory(output);
AppBuilder.Configure<App>().WithInterFont().With(DesktopFonts.Options()).UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
var bindingLog = new BindingLog(); Avalonia.Logging.Logger.Sink = bindingLog;
foreach (string scenario in new[] { "ready", "offline", "empty", "failed", "expired", "verification", "unknown" })
{
    using var vm = new MacMainViewModel(new MockWorkspace(scenario), new SubscriptionViewModel(new PreviewSubscriptionSession(scenario)));
    var initialized = vm.InitializeAsync();
    while (!initialized.IsCompleted) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(5); }
    initialized.GetAwaiter().GetResult();
    if (scenario == "failed") vm.PrepareCommand.ExecuteAsync(null).GetAwaiter().GetResult();
    if (scenario == "ready")
    {
        var row = vm.Applications.Last(); row.Category = "开发";
        if (!vm.Snapshot().ApplicationMetadata.Any(m => m.Path == row.Path && m.Category == "开发")) throw new InvalidOperationException("Unchecked application category was lost");
        var before = vm.Applications.ToArray(); vm.RefreshApplicationsCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        if (!before.SequenceEqual(vm.Applications)) throw new InvalidOperationException("Refresh replaced/reordered existing application rows");
        var savedBy = DateTime.UtcNow.AddSeconds(3);
        while (vm.ConfigStatus == "正在保存" && DateTime.UtcNow < savedBy) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
        if (vm.ConfigStatus == "正在保存") throw new InvalidOperationException("Mock autosave did not settle");
    }
    var window = new MainWindow { DataContext = vm, Width = 1280, Height = 840 };
    window.Show(); Dispatcher.UIThread.RunJobs();
    foreach (var page in vm.Pages)
    {
        vm.Navigate(page.Key); Dispatcher.UIThread.RunJobs();
        using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No frame");
        frame.Save(Path.Combine(output, scenario + "-" + page.Key + ".png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        if (scenario == "ready")
        {
            window.Width = 1024; window.Height = 680; Dispatcher.UIThread.RunJobs();
            using var compact = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No compact frame");
            compact.Save(Path.Combine(output, "compact-" + page.Key + ".png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            window.Width = 1280; window.Height = 840;
        }
    }
    if (scenario == "ready")
    {
        vm.Navigate("subscriptions"); Dispatcher.UIThread.RunJobs();
        var button = window.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == "检查会话");
        var point = button.TranslatePoint(new Point(button.Bounds.Width/2, button.Bounds.Height/2),window)!.Value;
        void SaveState(string state) { Dispatcher.UIThread.RunJobs(); using var image=window.CaptureRenderedFrame()!; image.Save(Path.Combine(output,"session-"+state+".png"),new Avalonia.Media.Imaging.PngBitmapEncoderOptions()); }
        window.MouseMove(point); SaveState("hover"); window.MouseDown(point,MouseButton.Left); SaveState("pressed"); window.MouseUp(point,MouseButton.Left);
        button.Focus(NavigationMethod.Tab); window.MouseMove(new Point(0,0)); SaveState("focused");
        vm.Subscriptions.Busy=true; SaveState("disabled"); vm.Subscriptions.Busy=false;
    }
    bindingLog.Verify(); window.Close(); Dispatcher.UIThread.RunJobs();
}
Console.WriteLine("Isolated macOS screenshots: " + output);

internal sealed class BindingLog : Avalonia.Logging.ILogSink
{
    private readonly List<string> _failures = [];
    public bool IsEnabled(Avalonia.Logging.LogEventLevel level,string area) => level >= Avalonia.Logging.LogEventLevel.Warning && area == "Binding";
    public void Log(Avalonia.Logging.LogEventLevel level,string area,object? source,string messageTemplate) => Log(level,area,source,messageTemplate,[]);
    public void Log(Avalonia.Logging.LogEventLevel level,string area,object? source,string messageTemplate,params object?[] values) { if(IsEnabled(level,area)) _failures.Add(messageTemplate + " " + string.Join(" | ",values)); }
    public void Verify() { if(_failures.Count>0)throw new InvalidOperationException(string.Join('\n',_failures.Distinct())); }
}
