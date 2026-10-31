using EgressController.Policy;

namespace EgressController.Runtime;

/// <summary>Subscription URLs and providers use one physical direct exit, independent of any core/helper state.</summary>
public sealed class DirectSubscriptionResources
{
    private readonly ProductionResources _resources;

    public DirectSubscriptionResources(IPhysicalSocketBinder binder, IInterfaceDnsResolver resolver,
        IValidatedResourceCache cache, InterfaceBinding binding, IProgress<PreparationProgress>? progress = null,
        Func<SocketsHttpHandler, HttpMessageHandler>? httpHandlerFactory = null)
    {
        var exit = ResourceExitSnapshot.DirectSubscription(binding);
        if (!exit.IsValid) throw new ArgumentException("Subscription downloads require a physical interface snapshot.", nameof(binding));
        _resources = new(new BoundHttpsResourceTransport(binder, resolver, httpHandlerFactory), cache,
            _ => ValueTask.FromResult(exit), progress);
    }

    public Task<ClashImportResult> FetchAsync(SubscriptionSource source, CancellationToken cancellationToken = default)
        => _resources.FetchSubscriptionAsync(source, PreparationCachePolicy.Refresh, cancellationToken);
}
