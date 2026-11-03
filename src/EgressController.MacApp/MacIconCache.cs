using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Media.Imaging;
using EgressController.Core.Protection;
using EgressController.Core.Storage;

namespace EgressController.MacApp;

/// <summary>Bounded, shared, off-thread icon extraction. Decoration never participates in route identity.</summary>
internal static class MacIconCache
{
    private static readonly ConcurrentDictionary<string, Task<Bitmap?>> Cache = new(StringComparer.Ordinal);
    private static readonly SemaphoreSlim Workers = new(2);
    public static Task<Bitmap?> LoadAsync(string? path)
    {
        RuntimeSafety.RequireLiveOperations();
        if (path is null || !File.Exists(path) || Cache.Count >= 192 && !Cache.ContainsKey(path)) return Task.FromResult<Bitmap?>(null);
        return Cache.GetOrAdd(path, p => Task.Run(() => ExtractAsync(p)));
    }
    private static async Task<Bitmap?> ExtractAsync(string path)
    {
        await Workers.WaitAsync().ConfigureAwait(false);
        try
        {
            string directory = Path.Combine(Mac.MacSettings.DataDirectory, "icons"); PrivateFile.EnsureDirectory(directory);
            string key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(path + File.GetLastWriteTimeUtc(path).Ticks)));
            string cached = Path.Combine(directory, key + ".png");
            if (!File.Exists(cached))
            {
                var start = new ProcessStartInfo("/usr/bin/sips") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var arg in new[] { "-s", "format", "png", "-Z", "64", path, "--out", cached }) start.ArgumentList.Add(arg);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using var process = Process.Start(start) ?? throw new IOException("Icon converter unavailable");
                var output = process.StandardOutput.ReadToEndAsync(timeout.Token); var error = process.StandardError.ReadToEndAsync(timeout.Token);
                try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); await Task.WhenAll(output,error).ConfigureAwait(false); }
                catch (OperationCanceledException) { if (!process.HasExited) process.Kill(); return null; }
                if (process.ExitCode != 0) return null;
            }
            return new Bitmap(cached);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
        finally { Workers.Release(); }
    }
}
