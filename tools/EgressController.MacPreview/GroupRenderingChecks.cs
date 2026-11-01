using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EgressController.Desktop.ViewModels;
using EgressController.Desktop.Views;
using EgressController.Desktop.Presentation.Controls;
using EgressController.Policy;
namespace EgressController.MacPreview;

internal static class GroupRenderingChecks
{
    public static void Run(string directory)
    {
        foreach (string product in new[] { "claude", "gemini", "openai", "github", "spotify", "tiktok", "youtube", "google", "microsoft", "apple", "paypal", "twitter", "telegram" })
            Check(ProductAvatar.Artwork(product) is not null, "Missing bundled artwork: " + product);
        var vm = new ShellViewModel("nodes");
        var doc = new MacPolicyDocument { Interfaces = vm.Document.Interfaces };
        for (int i = 0; i < 120; i++) doc.Nodes.Add(new() { Id = "stress-" + i, Name = $"香港 · 家宽 {i + 1:000}", Protocol = NodeProtocol.Trojan, Server = "node.example.invalid", Port = 443, Password = "isolated-fixture", Tls = true });
        string[] products = ["Claude", "Gemini", "OpenAI", "GitHub", "Spotify", "TikTok", "YouTube", "Google", "Microsoft", "Apple", "PayPal", "Twitter", "Telegram"];
        foreach (string product in products) doc.Groups.Add(new() { Id = "group-" + product.ToLowerInvariant(), Name = product, Kind = GroupKind.Select, MemberIds = doc.Nodes.Select(node => node.Id).Prepend(GroupGraph.Direct).ToList(), SelectedMemberId = doc.Nodes[0].Id });
        doc.Defaults.Global = ExitChoice.Proxy(doc.Groups[0].Id);
        vm.DslText = PolicyYaml.Write(doc); vm.ApplyDslAsync().GetAwaiter().GetResult(); Check(!vm.HasIssue, "Synthetic dense configuration failed to load.");
        var window = new MainWindow { DataContext = vm, Width = 1260, Height = 860 }; window.Show(); Flush();
        Save(window, Path.Combine(directory, "groups-dense-collapsed.png"));
        var group = vm.AllGroups[3]; group.Expanded = true; Flush();
        var list = window.GetVisualDescendants().OfType<ListBox>().Single(item => item.Name == "ProxyGroupList");
        list.ScrollIntoView(group); Flush();
        var scroll = list.GetVisualDescendants().OfType<ScrollViewer>().First();
        scroll.Offset = new Vector(0, 330); Flush();
        group.Expanded = false; Flush();
        var google = vm.AllGroups.Single(row => row.Name == "Google");
        var header = window.GetVisualDescendants().OfType<Button>().Single(button => button.DataContext == google && button.Classes.Contains("group-header"));
        // Reproduce the user's bottom-of-list Google click, including focus and pointer handling.
        scroll.Offset = new Vector(0, scroll.Extent.Height - scroll.Viewport.Height); Flush();
        double headerY = header.TranslatePoint(default, window)!.Value.Y;
        for (int repeat = 0; repeat < 3; repeat++)
        {
            var point = header.TranslatePoint(new Point(25, header.Bounds.Height / 2), window)!.Value;
            window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Flush();
            Check(google.Expanded == (repeat % 2 == 0), "Google header click did not toggle its own group.");
            Check(Math.Abs(header.TranslatePoint(default, window)!.Value.Y - headerY) < 1, "Google header moved when expanding/collapsing at the end of the list.");
        }
        Save(window, Path.Combine(directory, "groups-google-anchored.png"));
        google.Expanded = false; group.Expanded = true; Flush(); scroll.Offset = new Vector(0, 330); Flush();
        var offset = scroll.Offset; var groups = vm.VisibleGroups; var members = group.Members;
        var watch = Stopwatch.StartNew(); vm.TestGroupAsync(group.Id).GetAwaiter().GetResult(); Flush(); watch.Stop();
        Check(ReferenceEquals(groups, vm.VisibleGroups) && ReferenceEquals(members, group.Members), "Probe rebuilt row sources.");
        Check(Math.Abs(scroll.Offset.Y - offset.Y) < 1, "Probe jumped the scrolled group viewport.");
        Check(watch.Elapsed < TimeSpan.FromSeconds(5), "Synthetic 120-node result publication stalled the UI.");
        Console.WriteLine($"Dense group publication + render: {watch.ElapsedMilliseconds} ms; 120 nodes, 13 groups; viewport stable.");
        Save(window, Path.Combine(directory, "groups-dense-expanded-normal.png"));
        window.Width = 980; window.Height = 700; Flush(); Save(window, Path.Combine(directory, "groups-dense-expanded-compact.png"));
        foreach (var panel in window.GetVisualDescendants().OfType<MemberTilePanel>().Where(panel => panel.IsEffectivelyVisible))
            Check(panel.Children.All(child => child.Bounds.Right <= panel.Bounds.Width + 1), "Member grid overflowed compact width.");
        window.Close(); vm.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
    private static void Flush() { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    private static void Save(Window window, string path) { Flush(); using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No rendered frame"); frame.Save(path, new Avalonia.Media.Imaging.PngBitmapEncoderOptions()); }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
