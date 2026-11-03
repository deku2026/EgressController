using System.Text.Json.Serialization;

namespace EgressController.Subscriptions;
public enum LoginStatus { Unknown, Valid, Expired, NeedsVerification, NotSignedIn }
public sealed record LoginState(LoginStatus Status, DateTimeOffset CheckedAt, string Detail, DateTimeOffset? LastVerifiedAt = null);
public sealed class LoginRequiredException(LoginStatus status, string message) : InvalidOperationException(message)
{ public LoginStatus Status { get; } = status; }
public interface IInteractiveSubscriptionProvider : ISubscriptionProvider
{
    Task<LoginState> LoginAsync(SubscriptionSettings settings, ProviderCredentials credentials, string browserDirectory, IProgress<SubscriptionProgress> progress, CancellationToken token);
    Task<LoginState> CheckSessionAsync(SubscriptionSettings settings, string browserDirectory, CancellationToken token);
}
[JsonSerializable(typeof(LoginState))]
public sealed partial class LoginJson : JsonSerializerContext;
