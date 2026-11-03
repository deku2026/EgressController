using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace EgressController.Core.Storage;

public static class PrivateFile
{
    public static void EnsureDirectory(string directory)
    {
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
    public static async Task WriteAsync(string path, ReadOnlyMemory<byte> content, CancellationToken token = default)
    {
        EnsureDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.WriteThrough | FileOptions.Asynchronous };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(temporary, options))
            {
                await stream.WriteAsync(content, token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static Task WriteJsonAsync<T>(string path, T value, JsonTypeInfo<T> type, CancellationToken token = default)
        => WriteAsync(path, JsonSerializer.SerializeToUtf8Bytes(value, type), token);
    public static async Task<T?> ReadJsonAsync<T>(string path, JsonTypeInfo<T> type, CancellationToken token = default)
    {
        if (!File.Exists(path)) return default;
        using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync(stream, type, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("配置内容为空；原文件已保留。");
    }
}
