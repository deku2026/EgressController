using System.Text;
using EgressController.Diagnostics;

namespace EgressController.App.Services;

/// <summary>Writes only bounded diagnostic output beside the application executable.</summary>
public sealed class LocalLogSink
{
    private readonly object _gate = new();
    private readonly long _maxFileBytes;
    private readonly int _maxMessageLength;

    public LocalLogSink(string directory, long maxFileBytes = 5 * 1024 * 1024, int maxMessageLength = 32 * 1024)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("Log directory is required.", nameof(directory));
        if (maxFileBytes < 1024 || maxMessageLength < 128)
            throw new ArgumentOutOfRangeException(nameof(maxFileBytes));

        DirectoryPath = Path.GetFullPath(directory);
        Directory.CreateDirectory(DirectoryPath);
        LogPath = Path.Combine(DirectoryPath, "sing-box.log");
        _maxFileBytes = maxFileBytes;
        _maxMessageLength = maxMessageLength;
    }

    public string DirectoryPath { get; }
    public string LogPath { get; }

    public void Append(string source, string level, string message)
    {
        string bounded = (message ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (bounded.Length > _maxMessageLength)
            bounded = bounded[.._maxMessageLength] + "…";
        string line = $"{DateTimeOffset.UtcNow:O}\t{source}\t{level}\t{bounded}{Environment.NewLine}";

        lock (_gate)
        {
            RotateIfNeeded(line.Length * sizeof(char));
            using var stream = new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            writer.Write(line);
        }
    }

    public void Append(CoreLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Append(entry.Source, entry.Level, entry.Message);
    }

    private void RotateIfNeeded(long incomingBytes)
    {
        if (!File.Exists(LogPath))
            return;
        long existingBytes = new FileInfo(LogPath).Length;
        if (existingBytes + incomingBytes <= _maxFileBytes)
            return;

        string backup = LogPath + ".1";
        try
        {
            File.Move(LogPath, backup, overwrite: true);
        }
        catch (IOException)
        {
            // A reader may briefly hold the file. Truncate below so logging remains bounded.
            using var stream = new FileStream(LogPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        }
    }
}
