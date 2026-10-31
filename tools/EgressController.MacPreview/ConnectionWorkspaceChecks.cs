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

namespace EgressController.MacPreview;

/// <summary>Only synthetic observer facts and explicit fixture composition; never constructs a live backend.</summary>
internal static class ConnectionWorkspaceChecks
{
    public static void Run(string directory)
    {
        PreviewEnvironment.RequireIsolated();
        var shell = new ShellViewModel(); var vm = shell.Connections; string policy = PolicyYaml.Write(shell.Document);
        var window = new MainWindow { DataContext = shell, Width = 1260, Height = 860 }; window.Show(); Flush();
        Click(window, Find<Button>(window, "OverviewConnectionsTab")); Check(vm.IsOpen, "Overview connection tab did not receive pointer input.");
        var snapshot = PreviewConnectionFixtures.Create(); vm.Reconcile(snapshot); Flush();
        Check(vm.VisibleRows.Count == 180 && vm.Applications.Count == 7, "Connection snapshot did not publish all apps/rows.");
        var table = Find<ListBox>(window, "ConnectionTable"); var scroll = table.GetVisualDescendants().OfType<ScrollViewer>().First();
        Check(table.GetVisualDescendants().OfType<ListBoxItem>().Count() < 90, "Connection list did not virtualize its bounded viewport.");
        table.SelectedItem = vm.VisibleRows.First(row => row.Model.Activity == ConnectionActivity.Active); Flush();
        Save(window, Path.Combine(directory, "connections-normal.png"));
        window.Width = 980; window.Height = 700; Save(window, Path.Combine(directory, "connections-compact.png"));
        var displayButton = Find<Button>(window, "PauseConnectionDisplayButton"); var displayPoint = Center(window, displayButton);
        window.MouseMove(displayPoint); Save(window, Path.Combine(directory, "connections-controls-hover.png"));
        window.MouseDown(displayPoint, MouseButton.Left); Save(window, Path.Combine(directory, "connections-controls-pressed.png")); window.MouseUp(new Point(220, 10), MouseButton.Left); Flush();
        displayButton.Focus(NavigationMethod.Tab); Save(window, Path.Combine(directory, "connections-controls-focused.png"));

        var search = Find<TextBox>(window, "ConnectionSearch"); search.Focus(); window.KeyTextInput("session-0145"); Flush();
        Check(vm.VisibleRows.Count == 1 && vm.VisibleRows[0].Id == "connection-145", "Connection search did not filter actual bound input.");
        Save(window, Path.Combine(directory, "connections-search.png")); search.Text = ""; Flush();
        var appFilter = Find<ListBox>(window, "ConnectionApplicationFilter"); appFilter.SelectedItem = vm.Applications.Single(app => app.Name == "Claude"); Flush();
        Check(vm.VisibleRows.Count == 30 && vm.VisibleRows.All(row => row.ApplicationName == "Claude"), "Per-application filter combined records incorrectly.");
        Save(window, Path.Combine(directory, "connections-by-app.png")); appFilter.SelectedItem = vm.Applications[0]; Flush();
        Click(window, Find<Button>(window, "ActiveConnectionsButton"));
        Check(vm.VisibleRows.Count == 60 && vm.VisibleRows.All(row => row.Model.Activity == ConnectionActivity.Active), "Active view inferred liveness from missing end events.");
        Save(window, Path.Combine(directory, "connections-active.png")); Click(window, Find<Button>(window, "RecentConnectionsButton"));

        var sort = window.GetVisualDescendants().OfType<Button>().Single(button => ReferenceEquals(button.Command, vm.SortDownloadCommand)); Click(window, sort);
        Check(vm.VisibleRows[0].Model.DownloadedBytes == snapshot.Records.Max(record => record.DownloadedBytes), "Download header did not sort recorded counters.");
        vm.SortTimeCommand.Execute(null); Flush(); scroll.Offset = new Vector(0, 700); Flush();
        var rowControl = table.GetVisualDescendants().OfType<ListBoxItem>().First(item => item.IsEffectivelyVisible && Center(window, item).Y > Center(window, table).Y - 20 && Center(window, item).Y < Center(window, table).Y + 20);
        rowControl.Focus(NavigationMethod.Tab); table.SelectedItem = rowControl.DataContext; Flush();
        var selected = vm.SelectedConnection; var source = table.ItemsSource; var offset = scroll.Offset;
        Check(offset.Y > 0 && rowControl.IsFocused && selected is not null, "Connection viewport regression did not establish nonzero scrolling, selection and focus.");
        var changed = snapshot with { CapturedAt = snapshot.CapturedAt.AddSeconds(2), Records = snapshot.Records.Select(record => record with { LastObservedAt = record.LastObservedAt.AddSeconds(2), UploadedBytes = record.UploadedBytes + 1024 }).ToArray() };
        vm.Reconcile(changed); Flush();
        Check(ReferenceEquals(source, table.ItemsSource) && ReferenceEquals(selected, vm.SelectedConnection) && rowControl.IsFocused && scroll.Offset == offset, "Connection update reset row identity, selection, focus or viewport.");
        Point selectedPosition = Center(window, rowControl);
        var arrival = changed with { Records = changed.Records.Prepend(new("connection-arrival", changed.CapturedAt) { ApplicationId = "app-0", ApplicationName = "Claude", Host = "new-session.example.invalid", Transport = "tcp" }).ToArray() };
        vm.Reconcile(arrival); Flush();
        var focusedRow = table.ContainerFromItem(selected!) as ListBoxItem;
        Check(ReferenceEquals(selected, vm.SelectedConnection) && focusedRow?.IsFocused == true && Math.Abs(Center(window, focusedRow).Y - selectedPosition.Y) < 1, "A new connection shifted the inspected viewport or keyboard focus.");
        Save(window, Path.Combine(directory, "connections-refresh-preserved-viewport.png"));

        var pause = Find<Button>(window, "PauseConnectionDisplayButton"); Click(window, pause); Check(vm.IsPaused, "Pause display command failed.");
        string previousUploaded = selected!.Uploaded; var changedAgain = arrival with { CapturedAt = changed.CapturedAt.AddSeconds(2), Records = arrival.Records.Select(record => record with { UploadedBytes = record.UploadedBytes + 50000 }).ToArray() };
        vm.Reconcile(changedAgain); Check(selected.Uploaded == previousUploaded, "Paused display consumed a new traffic snapshot.");
        Save(window, Path.Combine(directory, "connections-paused.png")); Click(window, pause); Check(!vm.IsPaused && selected.Uploaded != previousUploaded, "Resume did not reconcile the latest pending snapshot.");
        vm.Query = "no-matching-observation.example"; Flush(); Check(vm.IsEmpty, "Connection empty filter state was not actionable."); Save(window, Path.Combine(directory, "connections-filter-empty.png")); vm.ResetFiltersCommand.Execute(null);
        vm.SelectedConnection = vm.VisibleRows.Single(row => row.Id == "connection-2"); Flush();
        Check(vm.SelectedConnection.Uploaded == "—" && vm.SelectedConnection.Duration == "—" && vm.SelectedConnection.ActivityLabel == "状态未知" && vm.SelectedConnection.ProcessPath.StartsWith("未知", StringComparison.Ordinal), "Sparse core observation fabricated traffic, liveness, duration or executable metadata.");
        window.Width = 1260; window.Height = 860; scroll.Offset = default; Save(window, Path.Combine(directory, "connections-unknown-details.png"));
        vm.TogglePauseCommand.Execute(null); vm.Reconcile(new([], changedAgain.CapturedAt.AddSeconds(2), false, "核心尚未提供可验证的连接观测；历史记录仅供检查。")); Flush();
        Check(!vm.IsAvailable && vm.VisibleRows.All(row => row.Model.Activity != ConnectionActivity.Active), "Observer failure left stale active/healthy facts during pause.");
        Save(window, Path.Combine(directory, "connections-unavailable.png"));
        Check(PolicyYaml.Write(shell.Document) == policy && !shell.HasChanges, "Passive connection viewing changed routing policy or dirty state."); window.Close();

        var production = new ConnectionWorkspaceViewModel(false); production.Reconcile(new([], snapshot.CapturedAt, true, "当前没有核心访问记录。"));
        var empty = new Window { Content = new ConnectionWorkspaceView { DataContext = production }, Width = 980, Height = 600, Padding = new Thickness(24) }; empty.Show();
        Save(empty, Path.Combine(directory, "connections-production-empty.png"));
        Check(!Find<Button>(empty, "PauseConnectionDisplayButton").IsEffectivelyEnabled, "Empty connection view did not show the disabled pause state.");
        Check(!empty.GetVisualDescendants().OfType<TextBlock>().Where(text => text.IsEffectivelyVisible).Any(text => (text.Text ?? "").Contains("合成", StringComparison.Ordinal) || (text.Text ?? "").Contains("预览", StringComparison.Ordinal)), "Empty production connection view leaked fixture labels.");
        empty.Close();
    }
    private static T Find<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().First(control => control.Name == name);
    private static Point Center(Window window, Control control) => control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window) ?? throw new InvalidOperationException("Missing preview coordinates.");
    private static void Click(Window window, Control control) { var point = Center(window, control); window.MouseMove(point); window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Flush(); }
    private static void Flush() { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    private static void Save(Window window, string path) { Flush(); using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No connection preview frame."); frame.Save(path, new PngBitmapEncoderOptions()); }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
