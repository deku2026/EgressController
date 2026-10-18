using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EgressController.App;
using EgressController.App.Preview;
using EgressController.App.ViewModels;
using EgressController.Core.Models;
using EgressController.Presentation;

namespace EgressController.UiPreview;

internal static class ApplicationChecks
{
    public static void Run(string directory)
    {
        using var white = WhiteFixture();
        if (!IconContrast.NeedsDarkSurface(white)) throw new InvalidOperationException("Transparent white artwork was not given a contrasting surface.");
        var panel = new StackPanel { Spacing = 16, Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "图标对比度 · 常态 / 悬停 / 选中 / 深色背景", FontSize = 18 });
        foreach (string surface in new[] { "#FFFFFF", "#F0F3F9", "#E9EEFA", "#344054" })
        {
            var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 20, Margin = new Thickness(14) };
            foreach (int size in new[] { 22, 28, 32, 48 })
            {
                row.Children.Add(new AppAvatar { Source = white, Width = size, Height = size });
                row.Children.Add(new AppAvatar { Source = PreviewIcons.Load("chatgpt"), Width = size, Height = size });
                row.Children.Add(new AppAvatar { Source = PreviewIcons.Load("claude"), Cli = true, Width = size, Height = size });
            }
            panel.Children.Add(new Border { Background = Brush.Parse(surface), CornerRadius = new CornerRadius(10), Child = row });
        }
        var iconsWindow = new Window { Width = 820, Height = 430, Content = panel }; iconsWindow.Show();
        RenderChecks.Save(iconsWindow, Path.Combine(directory, "application-icon-contrast.png")); iconsWindow.Close();

        var backend = new PreviewController(icons: key => key == "chatgpt" ? white : PreviewIcons.Load(key));
        var vm = new MainViewModel(backend);
        var window = new MainWindow { Width = 1440, Height = 900, DataContext = vm }; window.Show(); window.SelectPage("apps");
        RenderChecks.Save(window, Path.Combine(directory, "apps-white-artwork.png"));
        var combo = window.GetVisualDescendants().OfType<ComboBox>().First(item => item.Classes.Contains("category"));
        combo.IsDropDownOpen = true; Dispatcher.UIThread.RunJobs();
        var popup = combo.GetTemplateDescendants().OfType<Popup>().Single();
        var top = TopLevel.GetTopLevel(popup.Child!) ?? throw new InvalidOperationException("Category popup was not opened.");
        using (var bitmap = top.CaptureRenderedFrame() ?? throw new InvalidOperationException("No popup render"))
            bitmap.Save(Path.Combine(directory, "application-category-popup.png"), new PngBitmapEncoderOptions());
        combo.IsDropDownOpen = false;
        var game = vm.Apps.Entries.Single(item => item.Name == "英雄联盟");
        game.SelectedCategory = ApplicationCategory.BuiltIn.Single(item => item.Id == "games");
        game.Routing.SelectedRoute = game.Routing.Options.Single(item => item.Target.AdapterId == backend.Profile.ProxyAdapterId);
        game.Routing.IsSelected = true;
        if (backend.Profile.Applications.Single(item => item.DiscoveryKey == game.DiscoveryKey).Target.AdapterId != backend.Profile.ProxyAdapterId)
            throw new InvalidOperationException("Game route did not retain the explicitly selected second adapter.");
        vm.Apps.SelectedCategory = vm.Apps.Categories.Single(item => item.Id == "games");
        RenderChecks.Save(window, Path.Combine(directory, "apps-game-direct.png"));
        window.Width = 1100; window.Height = 760;
        RenderChecks.Save(window, Path.Combine(directory, "apps-game-direct-compact.png"));
        var inventoryButton = window.GetVisualDescendants().OfType<Button>().First(item => item.Classes.Contains("inventory"));
        var inventoryFlyout = (Flyout)inventoryButton.Flyout!;
        inventoryFlyout.ShowAt(inventoryButton); Dispatcher.UIThread.RunJobs();
        var inventoryTop = TopLevel.GetTopLevel((Control)inventoryFlyout.Content!) ?? throw new InvalidOperationException("Inventory details did not open.");
        using (var frame = inventoryTop.CaptureRenderedFrame() ?? throw new InvalidOperationException("No inventory render"))
            frame.Save(Path.Combine(directory, "application-inventory-popup.png"), new PngBitmapEncoderOptions());
        inventoryFlyout.Hide();
        vm.Apps.SelectedCategory = vm.Apps.Categories.Single(item => item.Id == "social");
        RenderChecks.Save(window, Path.Combine(directory, "apps-category-empty.png"));
        window.SelectPage("connections"); RenderChecks.Save(window, Path.Combine(directory, "connections-white-artwork.png"));
        window.SelectPage("traffic"); RenderChecks.Save(window, Path.Combine(directory, "traffic-white-artwork.png"));
        window.Close();

        var largeBackend = new PreviewController("empty");
        for (int i = 0; i < 500; i++) largeBackend.AddManualApplicationAsync($@"C:\Mock\App{i:000}\app.exe").GetAwaiter().GetResult();
        var largeVm = new MainViewModel(largeBackend);
        var largeWindow = new MainWindow { Width = 1100, Height = 760, DataContext = largeVm };
        largeWindow.Show(); largeWindow.SelectPage("apps"); Dispatcher.UIThread.RunJobs();
        using (largeWindow.CaptureRenderedFrame()) { }
        var list = largeWindow.GetVisualDescendants().OfType<ListBox>().Single(item => item.Name == "ApplicationList");
        var scroll = list.GetVisualDescendants().OfType<ScrollViewer>().First(); scroll.Offset = new Vector(0, 6000);
        Dispatcher.UIThread.RunJobs(); using (largeWindow.CaptureRenderedFrame()) { }
        double offset = scroll.Offset.Y;
        var visibleRow = list.GetVisualDescendants().OfType<CheckBox>().First().DataContext as AppEntryViewModel
            ?? throw new InvalidOperationException("Application rows were not realized.");
        visibleRow.SelectedCategory = ApplicationCategory.BuiltIn.Single(item => item.Id == "office");
        RenderChecks.Save(largeWindow, Path.Combine(directory, "apps-large-list.png"));
        if (offset < 1000 || Math.Abs(scroll.Offset.Y - offset) > 1) throw new InvalidOperationException("Changing category reset app-list scrolling.");
        if (list.GetVisualDescendants().OfType<AppAvatar>().Count() > 30) throw new InvalidOperationException("App list lost virtualization.");
        largeWindow.Close();
    }

    private static WriteableBitmap WhiteFixture()
    {
        // Synthetic transparent white shell-icon fixture; tests backing choice without recoloring any brand asset.
        var bitmap = new WriteableBitmap(new PixelSize(32, 32), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        using var pixels = bitmap.Lock(); byte[] bytes = new byte[pixels.RowBytes * 32];
        for (int y = 0; y < 32; y++) for (int x = 0; x < 32; x++)
        {
            double distance = Math.Sqrt((x - 15.5) * (x - 15.5) + (y - 15.5) * (y - 15.5));
            if (distance is < 9 or > 13 && !(x is >= 14 and <= 17 && y is >= 10 and <= 21)) continue;
            int offset = y * pixels.RowBytes + x * 4; bytes[offset] = bytes[offset + 1] = bytes[offset + 2] = bytes[offset + 3] = 255;
        }
        Marshal.Copy(bytes, 0, pixels.Address, bytes.Length); return bitmap;
    }
}
