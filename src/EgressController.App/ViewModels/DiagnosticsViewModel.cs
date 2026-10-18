using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using EgressController.Diagnostics;

namespace EgressController.App.ViewModels;

public sealed record RuntimeEventViewModel(CoreLogEntry Entry)
{
    public string Time => Entry.TimestampUtc.ToLocalTime().ToString("HH:mm:ss");
    public string Stage => Entry.Source;
    public string Message => Entry.Message.Split('\n')[0];
    public string Detail => $"{Entry.TimestampUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}\n{Entry.Source}\n\n{Entry.Message}";
    public bool Failed => Entry.Level is "error" or "fatal";
}

public sealed class DiagnosticIssueViewModel(DiagnosticIssue issue)
{
    public DiagnosticIssue Issue => issue;
    public string Summary => $"{issue.LastSeen.ToLocalTime():MM-dd HH:mm:ss} · {issue.Stage} · {issue.Code} · {issue.Count} 次";
    public string Message => issue.Message;
    public string Details => $"首次：{issue.FirstSeen.ToLocalTime():yyyy-MM-dd HH:mm:ss}\n"
        + $"最近：{issue.LastSeen.ToLocalTime():yyyy-MM-dd HH:mm:ss}\n阶段：{issue.Stage}\n"
        + $"错误码：{issue.Code}\n次数：{issue.Count}\n原因：{issue.Message}\n\n{issue.Detail}";
}

public sealed class DiagnosticsViewModel(DiagnosticJournal journal, BoundedLogStore coreLogs, string coreLogPath)
    : ObservableObject
{
    private long _version = -1, _logVersion = -1, _coreVersion = -1;
    private DiagnosticIssueViewModel? _selected;
    private string _latestFailure = "", _controllerLog = "", _coreLog = "";
    private RuntimeEventViewModel? _selectedEvent;
    public string DirectoryPath => journal.DirectoryPath;
    public string ControllerLogPath => journal.LogPath;
    public string CoreLogPath => coreLogPath;
    public ObservableCollection<DiagnosticIssueViewModel> Issues { get; } = [];
    public ObservableCollection<RuntimeEventViewModel> Events { get; } = [];
    public bool NoEvents => Events.Count == 0;
    public RuntimeEventViewModel? SelectedEvent
    {
        get => _selectedEvent;
        set { if (SetProperty(ref _selectedEvent, value)) OnPropertyChanged(nameof(EventDetail)); }
    }
    public string EventDetail => SelectedEvent?.Detail ?? "选择一条事件查看原因。";
    public string LatestFailure { get => _latestFailure; private set => SetProperty(ref _latestFailure, value); }
    public bool HasFailures => Issues.Count > 0;
    public string ControllerLog { get => _controllerLog; private set => SetProperty(ref _controllerLog, value); }
    public string CoreLog { get => _coreLog; private set => SetProperty(ref _coreLog, value); }
    public DiagnosticIssueViewModel? Selected
    {
        get => _selected;
        set { if (SetProperty(ref _selected, value)) OnPropertyChanged(nameof(SelectedDetails)); }
    }
    public string SelectedDetails => Selected?.Details ?? "尚未记录失败。";
    public string ExportText => $"保护日志：{ControllerLogPath}\n内核输出：仅保留在内存中\n\n"
        + string.Join("\n\n", Issues.Select(issue => issue.Details)) + "\n\n主程序最近日志：\n"
        + ControllerLog + "\n\n内核最近日志：\n" + CoreLog;

    public void Refresh()
    {
        long version = journal.Version;
        if (_version != version)
        {
            var selected = Selected?.Issue;
            Issues.Clear();
            foreach (var issue in journal.Snapshot()) Issues.Add(new(issue));
            Selected = Issues.FirstOrDefault(item => selected is not null && item.Issue.Stage == selected.Stage
                && item.Issue.Code == selected.Code && item.Issue.Detail == selected.Detail) ?? Issues.FirstOrDefault();
            LatestFailure = Issues.FirstOrDefault() is { } latest ? latest.Summary + "\n" + latest.Message : "";
            OnPropertyChanged(nameof(HasFailures));
            _version = version;
        }
        if (_logVersion != journal.Logs.Version)
        {
            _logVersion = journal.Logs.Version;
            ControllerLog = Format(journal.Logs.Snapshot());
            var previous = SelectedEvent?.Entry;
            Events.Clear();
            foreach (var entry in journal.Logs.Snapshot().Where(ProtectionLogPolicy.IsRelevant).TakeLast(200).Reverse()) Events.Add(new(entry));
            SelectedEvent = Events.FirstOrDefault(item => item.Entry == previous) ?? Events.FirstOrDefault();
            OnPropertyChanged(nameof(NoEvents));
        }
        if (_coreVersion != coreLogs.Version)
        {
            _coreVersion = coreLogs.Version;
            CoreLog = Format(coreLogs.Snapshot());
        }
    }
    private static string Format(IReadOnlyList<CoreLogEntry> entries)
    {
        var text = new System.Text.StringBuilder();
        foreach (var entry in entries.TakeLast(100).Reverse())
        {
            text.AppendLine($"{entry.TimestampUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} [{entry.Source}/{entry.Level}] {entry.Message}");
            if (text.Length <= 64 * 1024) continue;
            text.Length = 64 * 1024;
            text.AppendLine("\n更多内容请打开日志目录查看。");
            break;
        }
        return text.ToString();
    }
}
