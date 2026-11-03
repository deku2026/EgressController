namespace EgressController.Subscriptions;

public interface ISubscriptionSession
{
    bool IsPreview { get; }
    bool SupportsAutomaticChecks => false;
    Task<SubscriptionResult[]> ReadReceiptsAsync(CancellationToken token) => Task.FromResult<SubscriptionResult[]>([]);
    Task<LoginState> ReadLoginStateAsync(CancellationToken token);
    Task<LoginState> LoginAsync(SubscriptionSettings settings, IProgress<SubscriptionProgress> progress, CancellationToken token);
    Task<LoginState> CheckSessionAsync(SubscriptionSettings settings, CancellationToken token);
    Task<SubscriptionSettings> LoadAsync(CancellationToken token);
    Task SaveAsync(SubscriptionSettings settings, string username, string password, string ingressToken, CancellationToken token);
    Task<SubscriptionResult[]> RefreshAsync(SubscriptionSettings settings, IProgress<SubscriptionProgress> progress, CancellationToken token);
    Task<string> CheckHealthAsync(SubscriptionSettings settings, CancellationToken token);
}
