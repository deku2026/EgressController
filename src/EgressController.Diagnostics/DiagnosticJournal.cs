using System.Text;
using System.Text.RegularExpressions;

namespace EgressController.Diagnostics;

public sealed record DiagnosticIssue(DateTimeOffset FirstSeen, DateTimeOffset LastSeen,
    string Stage, string Code, string Message, string Detail, int Count);

/// <summary>Failures survive progress/success messages; a broken log disk cannot stop protection.</summary>
public sealed class DiagnosticJournal
{
    private readonly object _gate = new();
    private readonly List<DiagnosticIssue> _issues = [];
    private readonly Action<CoreLogEntry> _write;
    private readonly Func<DateTimeOffset> _now;
    private readonly int _capacity;
    private long _version;
    private DateTimeOffset _lastRepeatedWrite;
    public DiagnosticJournal(string path, Action<CoreLogEntry>? write = null,
        Func<DateTimeOffset>? now = null, int capacity = 200)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        LogPath = Path.GetFullPath(path);
        _write = write ?? new RotatingDiagnosticFile(LogPath).Append;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _capacity = capacity;
    }
    public string LogPath { get; }
    public string DirectoryPath => Path.GetDirectoryName(LogPath)!;
    public BoundedLogStore Logs { get; } = new();
    public long Version => Interlocked.Read(ref _version);
    public IReadOnlyList<DiagnosticIssue> Snapshot() { lock (_gate) return _issues.ToArray(); }
    public void Info(string stage, string message) => Write(stage, "info", Redact(message));

    public void Report(string stage, string code, string message, Exception? error = null)
    {
        string detail = Redact(error?.ToString() ?? message);
        message = Redact(message);
        lock (_gate)
        {
            DateTimeOffset now = _now();
            int index = _issues.FindIndex(item => item.Stage == stage && item.Code == code
                && item.Message == message && item.Detail == detail);
            int count = 1;
            DateTimeOffset first = now;
            if (index >= 0)
            {
                count = _issues[index].Count == int.MaxValue ? int.MaxValue : _issues[index].Count + 1;
                first = _issues[index].FirstSeen;
                _issues.RemoveAt(index);
            }
            _issues.Insert(0, new(first, now, stage, code, message, detail, count));
            if (_issues.Count > _capacity) _issues.RemoveAt(_issues.Count - 1);
            Interlocked.Increment(ref _version);
            // Keep every occurrence in the UI counter, bound identical disk messages during retries.
            if (index < 0 || now - _lastRepeatedWrite >= TimeSpan.FromSeconds(30))
            {
                _lastRepeatedWrite = now;
                Write(stage, "error", $"[{code}] {message}\n次数：{count}\n{detail}");
            }
        }
    }

    private void Write(string stage, string level, string message)
    {
        var entry = new CoreLogEntry(_now(), stage, level, Redact(message));
        Logs.Append(entry.Source, entry.Level, entry.Message, entry.TimestampUtc);
        try { _write(entry); }
        catch (Exception error)
        {
            // Do not call Report here: logging its own I/O failure would recurse.
            string detail = Redact($"无法写入日志：{LogPath}\n{error}");
            lock (_gate)
            {
                _issues.RemoveAll(issue => issue.Code == "log.write");
                _issues.Insert(0, new(entry.TimestampUtc, entry.TimestampUtc, "日志写入", "log.write",
                    "日志保存失败；错误仍保留在界面，可复制详情。", detail, 1));
                if (_issues.Count > _capacity) _issues.RemoveAt(_issues.Count - 1);
                Interlocked.Increment(ref _version);
            }
        }
    }

    public static string Redact(string value)
    {
        // Never export local controller bearer credentials, including exception/HTTP text.
        value = Regex.Replace(value, @"(?i)(bearer\s+)[^\s\""',;]+", "$1[redacted]", RegexOptions.NonBacktracking);
        value = Regex.Replace(value, @"(?i)((?:controller[_-]?secret|secret|password|api[_-]?key)[\""']?\s*[:=]\s*[\""']?)[^\s\""',;}]+",
            "$1[redacted]", RegexOptions.NonBacktracking);
        return value.Length > 32 * 1024 ? value[..(32 * 1024)] + "… [truncated]" : value;
    }
}

/// <summary>One current log and one previous log; independent files for main, service and tray.</summary>
public sealed class RotatingDiagnosticFile(string path, long maxBytes = 5 * 1024 * 1024)
{
    private readonly object _gate = new();
    public void Append(CoreLogEntry entry)
    {
        string text = $"{entry.TimestampUtc:O}\t{entry.Source}\t{entry.Level}\t"
            + entry.Message.Replace("\r", "").Replace("\n", "\n  ") + Environment.NewLine;
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (File.Exists(path) && new FileInfo(path).Length + Encoding.UTF8.GetByteCount(text) > maxBytes)
                File.Move(path, path + ".1", overwrite: true);
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.Write(text);
        }
    }
}
