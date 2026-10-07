using Orleans.Runtime;

namespace DigitalBrain.Apps;

// Only the app neuron a listing names may write it: Upsert's listing must address the calling
// app, and Remove may only take down the caller's own listing. A Remove of a listing that no
// longer exists is a no-op for anyone. A catalog vendor name is never anyone's to publish under.
internal sealed class MarketplaceWriteGuard(ShippedCatalog? catalog = null) : IIncomingGrainCallFilter
{
    public async Task Invoke(IIncomingGrainCallContext context)
    {
        if (context.Grain is MarketplaceNeuron marketplace
            && context.InterfaceMethod.Name is nameof(IMarketplace.Upsert) or nameof(IMarketplace.Remove))
        {
            if (context.InterfaceMethod.Name == nameof(IMarketplace.Upsert) && catalog is not null
                && context.Request.GetArgument(0) is MarketplaceListing listed
                && (await catalog.Listings()).Any(shipped => shipped.Publisher == listed.Publisher))
            { throw new UnauthorizedAccessException($"{listed.Publisher} is a vendor of shipped apps; no one publishes under it."); }
            // An Upsert always names its owner: a listing whose source is not a brain has no app to write it.
            var owner = context.InterfaceMethod.Name == nameof(IMarketplace.Upsert)
                ? MarketplaceNeuron.OwnerKey((MarketplaceListing)context.Request.GetArgument(0)!) ?? ""
                : marketplace.OwnerKey((string)context.Request.GetArgument(0)!, (string)context.Request.GetArgument(1)!);
            if (owner is not null
                && (context.SourceId is not { } source || source.Type.ToString() != DigitalBrain.Framework.AppGrainType.Name || source.Key.ToString() != owner))
            { throw new UnauthorizedAccessException("Only the app that owns a Marketplace listing may change it."); }
        }
        await context.Invoke();
    }
}
