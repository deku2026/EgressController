using EgressController.Windows.SystemProxy;

var manager = new SystemProxyManager();
var state = manager.Snapshot();
Console.WriteLine($"enabled={state.Enabled}");
Console.WriteLine($"server={state.Server ?? "(none)"}");
Console.WriteLine($"bypass={state.ProxyOverride ?? "(none)"}");
Console.WriteLine($"pac={state.AutoConfigUrl ?? "(none)"}");
Console.WriteLine($"wpad={state.AutoDetect}");

if (args.FirstOrDefault()?.Equals("watch", StringComparison.OrdinalIgnoreCase) == true)
{
    int seconds = args.Length > 1 && int.TryParse(args[1], out int parsed) ? Math.Clamp(parsed, 1, 300) : 10;
    using var watcher = manager.Watch(changed =>
    {
        Console.WriteLine($"changed enabled={changed.Enabled} server={changed.Server ?? "(none)"} pac={changed.AutoConfigUrl ?? "(none)"} wpad={changed.AutoDetect}");
    });
    Console.WriteLine($"watching={seconds}s");
    await Task.Delay(TimeSpan.FromSeconds(seconds));
}
