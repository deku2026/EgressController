using System.Text.Json.Serialization;
using EgressController.Core.Storage;

namespace EgressController.Subscriptions;

public sealed record HealthSnapshot(bool BackendHealthy, bool NetworkAvailable, NodeObservation[] Nodes, string Summary);
public interface INodeHealthSource { Task<HealthSnapshot> ObserveAsync(SubscriptionSettings settings, CancellationToken token); }
public sealed record ScheduleState(HealthState Health, DateTimeOffset CheckedAt, string Summary, DateTimeOffset? SessionAttempt = null);
[JsonSerializable(typeof(ScheduleState))]
public sealed partial class ScheduleJson : JsonSerializerContext;

/// <summary>Independent observation/refresh scheduler. Has no authority over TUN, protection, processes or Surge activation.</summary>
public sealed class SubscriptionScheduler(string root, ISubscriptionSession session, INodeHealthSource source)
{
    public async Task<string> CheckOnceAsync(bool allowRefresh, CancellationToken token)
    {
        string directory = Path.Combine(root, "subscriptions"); PrivateFile.EnsureDirectory(directory);
        await using var gate = new FileStream(Path.Combine(directory, "schedule.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var settings = await session.LoadAsync(token).ConfigureAwait(false);
        var previous = await PrivateFile.ReadJsonAsync(Path.Combine(directory, "schedule.json"), ScheduleJson.Default.ScheduleState, token).ConfigureAwait(false) ?? new(new(), default, "尚未检测");
        if (allowRefresh && !settings.AutomaticChecks) return "自动检测未启用";
        // GUI and launchd may both wake. One completed round per 15 minutes is authoritative.
        if (allowRefresh && DateTimeOffset.UtcNow - previous.CheckedAt < TimeSpan.FromMinutes(15)) return previous.Summary;
        var snapshot = await source.ObserveAsync(settings, token).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var policy = new HealthPolicy { Conditions = [new(settings.GroupPattern, "timeout-percent", settings.FailurePercent)], ConsecutiveRounds = settings.ConsecutiveRounds, Cooldown = TimeSpan.FromMinutes(settings.CooldownMinutes) };
        var decision = HealthEvaluator.Evaluate(policy, previous.Health, snapshot.Nodes, snapshot.BackendHealthy, snapshot.NetworkAvailable, now);
        var next = new ScheduleState(decision.State, now, $"{snapshot.Summary} · 已知 {decision.Known} / 未知 {decision.Unknown} · {decision.Reason}", previous.SessionAttempt);
        if (!allowRefresh) return next.Summary; // Manual diagnostics never advance automatic failure rounds or consume cooldown.
        var login = await session.ReadLoginStateAsync(token).ConfigureAwait(false);
        bool blocked = login.Status is LoginStatus.Expired or LoginStatus.NeedsVerification or LoginStatus.NotSignedIn;
        bool keepAlive = allowRefresh && settings.KeepSessionActive && !blocked && snapshot.BackendHealthy && snapshot.NetworkAvailable &&
            now - (previous.SessionAttempt ?? login.CheckedAt) >= TimeSpan.FromHours(6);
        // Persist attempt/cooldown before starting browser work; crashes cannot create a refresh loop.
        if (keepAlive) next = next with { SessionAttempt = now };
        await PrivateFile.WriteJsonAsync(Path.Combine(directory, "schedule.json"), next, ScheduleJson.Default.ScheduleState, token).ConfigureAwait(false);
        try
        {
            if (keepAlive)
            {
                login = await session.CheckSessionAsync(settings, token).ConfigureAwait(false);
                blocked = login.Status != LoginStatus.Valid;
            }
            if (allowRefresh && decision.Refresh && !blocked)
            {
                await session.RefreshAsync(settings, new Progress<SubscriptionProgress>(), token).ConfigureAwait(false);
                next = next with { Health = next.Health with { LastSuccess = DateTimeOffset.UtcNow }, Summary = "订阅获取、校验和服务器确认完成" };
            }
            else if (blocked) next = next with { Summary = next.Summary + " · 自动获取暂停，请手动确认登录会话" };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (LoginRequiredException) { next = next with { Summary = "会话失效或网站要求人工验证；自动获取暂停" }; }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException or System.Net.Http.HttpRequestException)
        { next = next with { Summary = "本轮操作未完成；保留有效订阅并进入冷却，可在界面查看会话状态" }; }
        await PrivateFile.WriteJsonAsync(Path.Combine(directory, "schedule.json"), next, ScheduleJson.Default.ScheduleState, token).ConfigureAwait(false);
        return next.Summary;
    }
    public async Task RunAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15));
        do
        {
            try { await CheckOnceAsync(true, token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException or TimeoutException) { /* Busy or invalid local settings: retry on next scheduled round only. */ }
        } while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false));
    }
}
