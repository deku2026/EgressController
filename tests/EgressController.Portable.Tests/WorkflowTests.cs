using System.Text;
using System.Security.Cryptography;
using EgressController.Subscriptions;
using EgressController.Surge;

namespace EgressController.Portable.Tests;
public sealed class WorkflowTests
{
    private static string Root() => Path.Combine(Path.GetTempPath(),"egress-tests-" + Guid.NewGuid().ToString("N"));
    [Fact] public async Task Failed_upload_retries_same_version_and_bytes_without_browser()
    {
        string root=Root(); var provider=new Provider(); var uploader=new Uploader { Fail=true };
        try
        {
            var workflow=new SubscriptionWorkflow(provider,new Validator(),uploader,root); var settings=new SubscriptionSettings();
            await Assert.ThrowsAsync<IOException>(()=>workflow.RefreshAsync(settings,new("",""),new Progress<SubscriptionProgress>(),TestContext.Current.CancellationToken));
            long first=uploader.Versions[0]; uploader.Fail=false;
            var result=await workflow.RefreshAsync(settings,new("",""),new Progress<SubscriptionProgress>(),TestContext.Current.CancellationToken);
            Assert.Equal(1,provider.Calls); Assert.All(uploader.Versions,v=>Assert.Equal(first,v)); Assert.Equal(2,result.Length); Assert.False(File.Exists(Path.Combine(root,"pending.json")));
        }
        finally { Directory.Delete(root,true); }
    }
    [Fact] public async Task Invalid_second_format_never_replaces_latest_or_uploads_first()
    {
        string root=Root(); Directory.CreateDirectory(root); await File.WriteAllTextAsync(Path.Combine(root,"latest.yaml"),"previous",TestContext.Current.CancellationToken); var uploader=new Uploader();
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(()=>new SubscriptionWorkflow(new Provider(),new Validator{RejectSurge=true},uploader,root).RefreshAsync(new(),new("",""),new Progress<SubscriptionProgress>(),TestContext.Current.CancellationToken));
            Assert.Empty(uploader.Versions); Assert.Equal("previous",await File.ReadAllTextAsync(Path.Combine(root,"latest.yaml"),TestContext.Current.CancellationToken));
        }
        finally { Directory.Delete(root,true); }
    }
    [Fact] public async Task Pending_upload_cannot_move_to_different_server()
    {
        string root=Root(); var settings=new SubscriptionSettings(); var provider=new Provider(); var workflow=new SubscriptionWorkflow(provider,new Validator(),new Uploader{Fail=true},root);
        try
        {
            await Assert.ThrowsAsync<IOException>(()=>workflow.RefreshAsync(settings,new("",""),new Progress<SubscriptionProgress>(),TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<InvalidOperationException>(()=>workflow.RefreshAsync(settings with { ServerEndpoint="https://other.example/" },new("",""),new Progress<SubscriptionProgress>(),TestContext.Current.CancellationToken)); Assert.Equal(1,provider.Calls);
        }
        finally { Directory.Delete(root,true); }
    }
    [Fact] public async Task Invalid_candidate_preserves_last_valid_and_restore_does_not_activate()
    {
        string root=Root(); var validator=new ConfigValidator(); var manager=new ManagedConfiguration(root,validator);
        try
        {
            await manager.PrepareAsync("first",TestContext.Current.CancellationToken); validator.Valid=false; await manager.PrepareAsync("bad",TestContext.Current.CancellationToken);
            Assert.Equal("first",await File.ReadAllTextAsync(manager.LastValidPath,TestContext.Current.CancellationToken)); Assert.Equal("first",await manager.RestoreCandidateAsync(TestContext.Current.CancellationToken));
        }
        finally { Directory.Delete(root,true); }
    }
    [Fact] public async Task Separate_managers_cannot_replace_a_candidate_during_validation()
    {
        string root = Root(); var token = TestContext.Current.CancellationToken;
        var validator = new WaitingValidator();
        var first = new ManagedConfiguration(root, validator).PrepareAsync("first", token);
        await validator.Entered.Task.WaitAsync(token);
        try
        {
            await Assert.ThrowsAsync<IOException>(() => new ManagedConfiguration(root, new ConfigValidator()).PrepareAsync("second", token));
            Assert.Equal("first", await File.ReadAllTextAsync(Path.Combine(root, "candidate.conf"), token));
        }
        finally { validator.Release.SetResult(); await first; Directory.Delete(root, true); }
    }
    private sealed class WaitingValidator : IConfigValidator
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ConfigValidation> CheckAsync(string path, CancellationToken token)
        { Entered.SetResult(); await Release.Task.WaitAsync(token); return new(true, "valid"); }
    }
    private sealed class Provider : ISubscriptionProvider
    { public int Calls; public Task<IReadOnlyList<CapturedSubscription>> CaptureAsync(SubscriptionSettings s,ProviderCredentials c,string d,IProgress<SubscriptionProgress> p,CancellationToken t) { Calls++; return Task.FromResult<IReadOnlyList<CapturedSubscription>>([new(SourceFormat.Clash,Encoding.UTF8.GetBytes("fixture-clash")),new(SourceFormat.Surge,Encoding.UTF8.GetBytes("fixture-surge"))]); } }
    private sealed class Validator : ISubscriptionValidator
    { public bool RejectSurge; public Task ValidateAsync(SourceFormat f,string p,CancellationToken t) => f==SourceFormat.Surge&&RejectSurge ? Task.FromException(new InvalidDataException("invalid")):Task.CompletedTask; }
    private sealed class Uploader : ISubscriptionUploader
    { public bool Fail; public List<long> Versions=[]; public Task<SubscriptionResult> UploadAsync(string p,string c,SourceFormat f,byte[] b,long v,DateTimeOffset at,CancellationToken t) { Versions.Add(v); if(Fail)throw new IOException("test failure"); return Task.FromResult(new SubscriptionResult(f,Convert.ToHexStringLower(SHA256.HashData(b)),"revision",v)); } }
    private sealed class ConfigValidator : IConfigValidator
    { public bool Valid=true; public Task<ConfigValidation> CheckAsync(string path,CancellationToken token) => Task.FromResult(new ConfigValidation(Valid,Valid?"valid":"invalid")); }
}
