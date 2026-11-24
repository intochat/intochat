using System.Text.Json;
using IntoChat.OS;
using Orleans.Runtime;

namespace IntoChat.OS.Modules.Apps;

// Only the app neuron a listing names may write it or take it down: Upsert is checked against the listing in the call,
// Remove against the listing the marketplace holds. A features owner name is never anyone's to publish under.
internal sealed class MarketplaceWriteGuard(FeatureCatalog catalog, IServiceProvider services) : IIncomingGrainCallFilter
{
    public async Task Invoke(IIncomingGrainCallContext context)
    {
        // The marketplace is a plain neuron (06-08): its calls arrive on the runtime's host grain as one RuntimeInvocation.
        if (context.Grain is DigitalBrain.Runtime.Orleans.Hosting.INeuronHost
            && context.Request.GetArgumentCount() > 0 && context.Request.GetArgument(0) is DigitalBrain.Invocation.RuntimeInvocation call
            && DigitalBrain.NeuronId.TryParse(call.Key, out var target) && target.Type == MarketplaceType)
        {
            var json = DigitalBrain.Invocation.RuntimeProtocol.Json;
            if (call.Method == nameof(IMarketplace.Upsert))
            {
                var listed = call.Arguments.Length > 0 ? call.Arguments[0].Deserialize<MarketplaceListing>(json) : null;
                if (listed is not null && await catalog.IsOwner(listed.Publisher))
                { throw new UnauthorizedAccessException($"{listed.Publisher} owns the shipped catalog's apps; no one publishes under it."); }
                Require(context, listed is null ? "" : MarketplaceNeuron.OwnerKey(listed) ?? "");
            }
            else if (call.Method == nameof(IMarketplace.Remove)
                && call.Arguments.Length > 1
                && call.Arguments[0].Deserialize<string>(json) is { Length: > 0 } publisher
                && call.Arguments[1].Deserialize<string>(json) is { Length: > 0 } name
                && await Stored(target, publisher, name) is { } held)
            {
                // Nothing held under that name leaves nothing to take down; the neuron answers that call itself.
                Require(context, MarketplaceNeuron.OwnerKey(held) ?? "");
            }
        }
        await context.Invoke();
    }

    private const string MarketplaceType = "apps.marketplace";

    // The listing as the marketplace holds it, read as the marketplace's own module: Read interleaves, so the turn this
    // filter guards does not wait on itself.
    private async Task<MarketplaceListing?> Stored(DigitalBrain.NeuronId marketplace, string publisher, string name)
    {
        var edge = services.GetService(typeof(DigitalBrain.Access.EdgeAuthority)) as DigitalBrain.Access.EdgeAuthority;
        using (edge?.Enter(edge.Module(MarketplaceType, marketplace.Brain)))
        {
            return await DigitalBrain.Runtime.Orleans.Hosting.SiloNeurons.Get<IMarketplace>(services, marketplace).Read(publisher, name);
        }
    }

    // The calling grain is read from the message, and a cluster client writes its own sender id there: only a call this
    // silo process sent (SiloOrigin) names a grain that really made it.
    private static void Require(IIncomingGrainCallContext context, string? owner)
    {
        if (owner is not null
            && (!DigitalBrain.Runtime.Orleans.Access.SiloOrigin.IsCurrent
                || SourceOf(context.SourceId) is not { } source || source.Type != IntoChat.OS.Host.Primitives.AppGrainType.Name || source.Key != owner))
        { throw new UnauthorizedAccessException("Only the app that owns a Marketplace listing may change it."); }
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
            return DigitalBrain.NeuronId.TryParse(key, out var neuron) ? (neuron.Type, neuron.FullKey()) : null;
        }
        return (type, key);
    }
}
