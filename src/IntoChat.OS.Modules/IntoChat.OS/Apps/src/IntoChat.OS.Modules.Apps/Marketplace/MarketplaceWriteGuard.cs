using System.Text.Json;
using Orleans.Runtime;

namespace IntoChat.OS.Modules.Apps;

// Only the app neuron a listing names may write it. Marketplace storage returns in 0.8.0, so Remove
// has no stored listing to check. A features owner name is never anyone's to publish under.
internal sealed class MarketplaceWriteGuard(FeatureCatalog catalog) : IIncomingGrainCallFilter
{
    public async Task Invoke(IIncomingGrainCallContext context)
    {
        // The marketplace is a plain neuron (06-08): its calls arrive on the runtime's host grain as one RuntimeInvocation.
        if (context.Grain is global::DigitalBrain.Runtime.Orleans.Hosting.INeuronHost
            && context.Request.GetArgumentCount() > 0 && context.Request.GetArgument(0) is global::DigitalBrain.Invocation.RuntimeInvocation call
            && global::DigitalBrain.NeuronId.TryParse(call.Key, out var target) && target.Type == MarketplaceType
            && call.Method == nameof(IMarketplace.Upsert))
        {
            var json = global::DigitalBrain.Invocation.RuntimeProtocol.Json;
            var listed = call.Arguments.Length > 0 ? call.Arguments[0].Deserialize<MarketplaceListing>(json) : null;
            if (listed is not null && await catalog.IsOwner(listed.Publisher))
            { throw new UnauthorizedAccessException($"{listed.Publisher} owns the features folder's apps; no one publishes under it."); }
            Require(context, listed is null ? "" : MarketplaceNeuron.OwnerKey(listed) ?? "");
        }
        await context.Invoke();
    }

    private const string MarketplaceType = "apps.marketplace";

    private static void Require(IIncomingGrainCallContext context, string? owner)
    {
        if (owner is not null
            && (SourceOf(context.SourceId) is not { } source || source.Type != IntoChat.OS.Connections.AppGrainType.Name || source.Key != owner))
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
            return global::DigitalBrain.NeuronId.TryParse(key, out var neuron) ? (neuron.Type, $"{neuron.Brain.Value}/{neuron.Key}") : null;
        }
        return (type, key);
    }
}
