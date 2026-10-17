using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace EgressController.Guardian;

internal static class GuardStateFile
{
    public static T? Read<T>(string file, JsonTypeInfo<T> type)
    {
        try
        {
            using var stateLock = Lock(file, write: false);
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize(stream, type) ?? throw new InvalidDataException("守护状态文件为空：" + file);
        }
        catch (FileNotFoundException) { return default; }
        catch (DirectoryNotFoundException) { return default; }
    }
    public static void Write<T>(string file, T value, JsonTypeInfo<T> type)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        using var stateLock = Lock(file, write: true);
        string temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, value, type);
                stream.Flush(true);
            }
            // A sidecar file lock serializes cross-process access; the atomic replacement
            // also prevents partial JSON after a crash. Retry brief antivirus sharing conflicts.
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    if (File.Exists(file)) File.Replace(temporary, file, null);
                    else File.Move(temporary, file);
                    break;
                }
                catch (Exception error) when (attempt < 4 && error is IOException or UnauthorizedAccessException)
                { Thread.Sleep(10); }
            }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static FileStream? Lock(string file, bool write)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return new FileStream(file + ".lock", write ? FileMode.OpenOrCreate : FileMode.Open,
                    write ? FileAccess.ReadWrite : FileAccess.Read, write ? FileShare.None : FileShare.Read);
            }
            catch (FileNotFoundException) when (!write) { return null; } // First installation/legacy file.
            catch (DirectoryNotFoundException) when (!write) { return null; }
            catch (IOException) when (attempt < 99) { Thread.Sleep(10); }
        }
    }
}
