using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
namespace EgressController.MacApp.Preview;
public static class MockIcons
{
    public static IImage? Load(string name)
    {
        string? key = name switch { "Claude" or "Claude CLI" => "claude", "ChatGPT" => "chatgpt", "Google Chrome" => "chrome", "Steam" => "steam", _ => null };
        if (key is null) return null;
        using var stream = AssetLoader.Open(new Uri($"avares://EgressController.MacApp/Assets/{key}.png"));
        return new Bitmap(stream);
    }
}
