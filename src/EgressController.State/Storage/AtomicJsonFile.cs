using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace EgressController.State.Storage;

/// <summary>
/// Crash-safe, AOT-supported JSON persistence (plan §Step 12 §1.2): write to a temp file, flush
/// to disk, back up the previous version, then atomically move over the target. Corrupt files are
/// quarantined (renamed) rather than left to poison the next read.
/// </summary>
public static class AtomicJsonFile
{
    public static string Write<T>(string path, T value, JsonTypeInfo<T> jsonTypeInfo)
    {
        string full = Path.GetFullPath(path);
        string? dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, jsonTypeInfo);
        string tmp = full + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.WriteThrough))
        {
            fs.Write(bytes, 0, bytes.Length);
            fs.Flush(flushToDisk: true);
        }

        if (File.Exists(full))
            File.Copy(full, full + ".bak", overwrite: true);
        File.Move(tmp, full, overwrite: true);
        return full;
    }

    /// <summary>Read deserialize; JSON/IO failure triggers quarantine + returns the fallback.</summary>
    public static T Read<T>(string path, JsonTypeInfo<T> jsonTypeInfo, T fallback, Action<string>? onQuarantine = null)
    {
        if (!File.Exists(path))
            return fallback;
        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            T? value = JsonSerializer.Deserialize(bytes, jsonTypeInfo);
            return value is null ? fallback : value;
        }
        catch (Exception ex) when (ex is JsonException or IOException or ArgumentException)
        {
            string damaged = path + ".corrupt." + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            try { File.Move(path, damaged, overwrite: true); } catch { /* keep original */ }
            onQuarantine?.Invoke(damaged);
            return fallback;
        }
    }
}