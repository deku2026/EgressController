using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EgressController.Core.Models;
using EgressController.Core.Profile;
using EgressController.Diagnostics;
using EgressController.Rules.Catalog;
using EgressController.State.Quota;

namespace EgressController.App.ViewModels;

public sealed class ConnectionRowViewModel : ObservableObject
{
    private ConnectionObservation _item;
    private bool _isActive;
    private bool _ratesFresh = true;
    private Services.ProcessVisual? _appearance;
    public Services.ProcessVisual? Appearance { get => _appearance; private set => SetProperty(ref _appearance, value); }
    public void SetAppearance(Services.ProcessVisual appearance) { Appearance = appearance; OnPropertyChanged(nameof(ProcessName)); }
    private Func<string?, bool, string>? _routeName;
    public void SetRouteNames(Func<string?, bool, string> names) { _routeName = names; OnPropertyChanged(nameof(Route)); OnPropertyChanged(nameof(RoutePath)); }

    public ConnectionRowViewModel(ConnectionObservation item, bool active)
    {
        _item = item;
        _isActive = active;
    }

    public ConnectionObservation Observation => _item;
    public string Id => _item.Id;
    public bool IsActive => _isActive;
    public string UploadText => TrafficFormat.Bytes(_item.Upload);
    public string DownloadText => TrafficFormat.Bytes(_item.Download);
    public bool IsCurrent => _isActive && _ratesFresh;
    public string Status => !IsActive ? "已结束" : IsCurrent ? "活动" : "上次活动";
    public string ProcessName => Appearance?.Name ?? (string.IsNullOrWhiteSpace(_item.ProcessPath)
        ? "未识别进程"
        : Path.GetFileName(_item.ProcessPath));
    public string ProcessPath => string.IsNullOrWhiteSpace(_item.ProcessPath) ? "未识别路径" : _item.ProcessPath;
    public string Target => Endpoint(_item.Host, _item.DestinationIp, _item.DestinationPort);
    public string SourceEndpoint => Endpoint(string.Empty, _item.SourceIp, _item.SourcePort);
    public string DestinationEndpoint => Endpoint(string.Empty, _item.DestinationIp, _item.DestinationPort);
    public string Protocol => string.IsNullOrWhiteSpace(_item.Network) ? "未知" : _item.Network.ToUpperInvariant();
    public string Type => string.IsNullOrWhiteSpace(_item.Type) ? "未知" : _item.Type;
    public string RawRoute => _item.Outbound ?? _item.Chains.FirstOrDefault() ?? "未知出口";
    public string Route => _routeName?.Invoke(RawRoute, false) ?? RawRoute;
    public string RoutePath => _item.Chains.Count == 0 ? _routeName?.Invoke(RawRoute, true) ?? Route : string.Join(" → ", _item.Chains.Select(tag => _routeName?.Invoke(tag, true) ?? tag));
    public string Reason => string.IsNullOrWhiteSpace(_item.Rule) ? "final" : _item.Rule;
    public string Rule => string.IsNullOrWhiteSpace(_item.Rule) ? "未提供" : _item.Rule;
    public string DnsMode => string.IsNullOrWhiteSpace(_item.DnsMode) ? "未提供" : _item.DnsMode;
    public string Traffic => TrafficFormat.Bytes(TrafficFormat.AddCounters(_item.Upload, _item.Download));
    public string Speed => _isActive && _ratesFresh && _item.HasRate ? TrafficFormat.Rate(TrafficFormat.AddCounters(_item.UploadRate, _item.DownloadRate)) : "—";
    public void SetRateFreshness(bool fresh) { _ratesFresh = fresh; OnPropertyChanged(nameof(Speed)); OnPropertyChanged(nameof(IsCurrent)); OnPropertyChanged(nameof(Status)); }
    public string Duration => TrafficFormat.Duration(DurationValue());
    public string StartedAt => _item.StartedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff zzz");
    public string ClosedAt => _item.ClosedAtUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff zzz") ?? "仍在活动";

    public void Update(ConnectionObservation item, bool active)
    {
        _item = item;
        bool activeChanged = _isActive != active;
        _isActive = active;
        if (activeChanged)
            OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(ProcessName));
        OnPropertyChanged(nameof(ProcessPath));
        OnPropertyChanged(nameof(Target));
        OnPropertyChanged(nameof(SourceEndpoint));
        OnPropertyChanged(nameof(DestinationEndpoint));
        OnPropertyChanged(nameof(Protocol));
        OnPropertyChanged(nameof(Type));
        OnPropertyChanged(nameof(Route));
        OnPropertyChanged(nameof(RoutePath));
        OnPropertyChanged(nameof(Reason));
        OnPropertyChanged(nameof(Rule));
        OnPropertyChanged(nameof(DnsMode));
        OnPropertyChanged(nameof(Traffic));
        OnPropertyChanged(nameof(UploadText)); OnPropertyChanged(nameof(DownloadText));
        OnPropertyChanged(nameof(Speed));
        OnPropertyChanged(nameof(Duration));
        OnPropertyChanged(nameof(StartedAt));
        OnPropertyChanged(nameof(ClosedAt));
    }

    public void RefreshDuration() => OnPropertyChanged(nameof(Duration));

    private TimeSpan DurationValue()
    {
        DateTimeOffset end = _item.ClosedAtUtc ?? DateTimeOffset.UtcNow;
        return end > _item.StartedAtUtc ? end - _item.StartedAtUtc : TimeSpan.Zero;
    }

    private static string Endpoint(string? host, string? ip, string? port)
    {
        string address = !string.IsNullOrWhiteSpace(host) ? host : ip ?? string.Empty;
        if (address.Length == 0)
            return string.IsNullOrWhiteSpace(port) ? "未知目标" : $":{port}";
        if (address.Contains(':') && !address.StartsWith("[", StringComparison.Ordinal))
            address = $"[{address}]";
        return string.IsNullOrWhiteSpace(port) ? address : $"{address}:{port}";
    }
}

internal static class TrafficFormat
{
    public static long AddCounters(long left, long right)
    {
        left = Math.Max(0, left);
        right = Math.Max(0, right);
        return left > long.MaxValue - right ? long.MaxValue : left + right;
    }

    public static string Bytes(long value) => TrafficUnits.Bytes(Math.Max(0, value));

    public static string Rate(long value) => Bytes(value) + "/s";

    public static string UpdatedAt(DateTimeOffset? value)
        => value is null ? "等待 sing-box API" : value.Value.ToLocalTime().ToString("HH:mm:ss");

    public static string Duration(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
            value = TimeSpan.Zero;
        if (value.TotalDays >= 1)
            return $"{(int)value.TotalDays}d {value.Hours:00}:{value.Minutes:00}:{value.Seconds:00}";
        return value.TotalHours >= 1
            ? $"{value.Hours:00}:{value.Minutes:00}:{value.Seconds:00}"
            : $"{value.Minutes:00}:{value.Seconds:00}";
    }
}
internal static class QuotaFormat
{
    private const decimal BytesPerGigabyte = 1024m * 1024m * 1024m;

    public static bool TryParseGigabytes(string? text, out decimal value)
        => decimal.TryParse(
            text?.Trim(),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out value)
            && value >= 0
            && value <= long.MaxValue / BytesPerGigabyte;

    public static string Gigabytes(long bytes)
        => (Math.Max(0, bytes) / BytesPerGigabyte).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
}
