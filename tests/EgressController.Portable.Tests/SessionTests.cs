using EgressController.Core.Contracts;
using EgressController.Subscriptions;
namespace EgressController.Portable.Tests;

public sealed class SessionTests
{
    [Fact] public async Task Expiry_requires_an_actual_login_redirect_and_preserves_last_valid_time()
    {
        string root=Path.Combine(Path.GetTempPath(),"egress-session-"+Guid.NewGuid().ToString("N")); var provider=new Provider(); var session=new DesktopSubscriptionSession(root,new Secrets(),provider); var token=TestContext.Current.CancellationToken;
        try
        {
            provider.State=LoginStatus.NotSignedIn; Assert.Equal(LoginStatus.NotSignedIn,(await session.CheckSessionAsync(new(),token)).Status);
            provider.State=LoginStatus.Valid; var valid=await session.CheckSessionAsync(new(),token); Assert.NotNull(valid.LastVerifiedAt);
            provider.Fail=true; var unknown=await session.CheckSessionAsync(new(),token); Assert.Equal(LoginStatus.Unknown,unknown.Status); Assert.Equal(valid.LastVerifiedAt,unknown.LastVerifiedAt);
            provider.Fail=false; provider.State=LoginStatus.NeedsVerification; Assert.Equal(LoginStatus.NeedsVerification,(await session.CheckSessionAsync(new(),token)).Status);
            provider.State=LoginStatus.NotSignedIn; Assert.Equal(LoginStatus.Expired,(await session.CheckSessionAsync(new(),token)).Status);
            Assert.Equal(LoginStatus.Expired,(await new DesktopSubscriptionSession(root,new Secrets(),provider).ReadLoginStateAsync(token)).Status);
        }
        finally { Directory.Delete(root,true); }
    }
    private sealed class Secrets : ISecretStore { public string? Read(string key)=>null; public void Write(string key,string value) { } }
    private sealed class Provider : IInteractiveSubscriptionProvider
    {
        public LoginStatus State; public bool Fail;
        public Task<LoginState> CheckSessionAsync(SubscriptionSettings s,string d,CancellationToken t) => Fail?Task.FromException<LoginState>(new IOException("offline")):Task.FromResult(new LoginState(State,DateTimeOffset.UtcNow,"fixture"));
        public Task<LoginState> LoginAsync(SubscriptionSettings s,ProviderCredentials c,string d,IProgress<SubscriptionProgress> p,CancellationToken t)=>CheckSessionAsync(s,d,t);
        public Task<IReadOnlyList<CapturedSubscription>> CaptureAsync(SubscriptionSettings s,ProviderCredentials c,string d,IProgress<SubscriptionProgress> p,CancellationToken t)=>throw new NotSupportedException();
    }
}
