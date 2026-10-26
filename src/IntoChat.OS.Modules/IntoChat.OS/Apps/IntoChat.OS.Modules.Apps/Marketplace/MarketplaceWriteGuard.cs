using IntoChat.OS.Connections.Platform.Marketplace;
using Orleans.Runtime;

namespace IntoChat.OS.Modules.Apps;

// Only the app neuron a listing names may write it: Upsert's listing must address the calling
// app, and Remove may only take down the caller's own listing. A Remove of a listing that no
// longer exists is a no-op for anyone. A features owner name is never anyone's to publish under.
internal sealed class MarketplaceWriteGuard(FeatureCatalog catalog, IMarketplaceClient listings) : IIncomingGrainCallFilter
{
    public async Task Invoke(IIncomingGrainCallContext context)
    {
        if (context.Grain is MarketplaceNeuron
            && context.InterfaceMethod.Name is nameof(IMarketplace.Upsert) or nameof(IMarketplace.Remove))
        {
            if (context.InterfaceMethod.Name == nameof(IMarketplace.Upsert)
                && context.Request.GetArgument(0) is MarketplaceListing listed
                && await catalog.IsOwner(listed.Publisher))
            { throw new UnauthorizedAccessException($"{listed.Publisher} owns the features folder's apps; no one publishes under it."); }
            // An Upsert always names its owner: a listing whose source is not a brain has no app to write it.
            var owner = context.InterfaceMethod.Name == nameof(IMarketplace.Upsert)
                ? MarketplaceNeuron.OwnerKey((MarketplaceListing)context.Request.GetArgument(0)!) ?? ""
                : await listings.Read((string)context.Request.GetArgument(0)!, (string)context.Request.GetArgument(1)!) is { } stored
                    ? MarketplaceNeuron.OwnerKey(MarketplaceNeuron.Listing(stored))
                    : null;
            if (owner is not null
                && (SourceOf(context.SourceId) is not { } source || source.Type != IntoChat.OS.Connections.AppGrainType.Name || source.Key != owner))
            { throw new UnauthorizedAccessException("Only the app that owns a Marketplace listing may change it."); }
        }
        await context.Invoke();
    }

    // The calling grain's type and os key, read from the ids Orleans sets on the message (never from the call's body). The app is a
    // plain neuron (06-08): its call leaves from its runtime host grain, keyed by its neuron id "brain/type/key".
    private static (string Type, string Key)? SourceOf(GrainId? source)
    {
        if (source is not { } id) { return null; }
        var type = id.Type.ToString()!;
        var key = id.Key.ToString()!;
        if (type.StartsWith("neuron.host", StringComparison.Ordinal))
        {
            return global::DigitalBrain.NeuronId.TryParse(key, out var neuron) ? (neuron.Type, $"{neuron.Brain.Value}/{neuron.Key}") : null;
        }
        return (type, key);
    }
}
