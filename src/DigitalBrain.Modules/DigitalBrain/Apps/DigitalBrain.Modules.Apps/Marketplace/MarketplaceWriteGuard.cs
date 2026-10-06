using Orleans.Runtime;

namespace DigitalBrain.Apps;

// Only the app neuron a listing names may write it: Upsert's listing must address the calling
// app, and Remove may only take down the caller's own listing. A Remove of a listing that no
// longer exists is a no-op for anyone.
internal sealed class MarketplaceWriteGuard : IIncomingGrainCallFilter
{
    public async Task Invoke(IIncomingGrainCallContext context)
    {
        if (context.Grain is MarketplaceNeuron marketplace
            && context.InterfaceMethod.Name is nameof(IMarketplace.Upsert) or nameof(IMarketplace.Remove))
        {
            var owner = context.InterfaceMethod.Name == nameof(IMarketplace.Upsert)
                ? MarketplaceNeuron.OwnerKey((MarketplaceListing)context.Request.GetArgument(0)!)
                : marketplace.OwnerKey((string)context.Request.GetArgument(0)!, (string)context.Request.GetArgument(1)!);
            if (owner is not null
                && (context.SourceId is not { } source || source.Type.ToString() != DigitalBrain.Framework.AppGrainType.Name || source.Key.ToString() != owner))
            { throw new UnauthorizedAccessException("Only the app that owns a Marketplace listing may change it."); }
        }
        await context.Invoke();
    }
}
