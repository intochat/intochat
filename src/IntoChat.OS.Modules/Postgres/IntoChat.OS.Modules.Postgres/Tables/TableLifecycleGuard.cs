using System.Text.Json;
using DigitalBrain.Invocation;
using IntoChat.OS.Connections;
using IntoChat.OS.Framework;

namespace IntoChat.OS.Modules.Postgres.Tables;

// Every index and lifetime call names its owner; the guard checks the owner is this brain's and
// the source is the one grain type allowed to make that call. The table is a plain neuron (06-05b): a call it makes leaves
// from its runtime host grain, whose key is its neuron id, and a lifetime call reaches it on that host as a RuntimeInvocation.
// Both are read from the ids Orleans sets on the message (never from the call's body), so the rule is the grain-era one.
internal sealed class TableLifecycleGuard : IIncomingGrainCallFilter
{
    private static readonly string Lifetime = NeuronProxy.ContractName(typeof(ITableLifetime));

    public async Task Invoke(IIncomingGrainCallContext context)
    {
        var contract = context.InterfaceMethod.DeclaringType;
        if (contract == typeof(ITableIndex) && context.InterfaceMethod.Name == nameof(ITableIndex.ReadTables))
        {
            var owner = TableOwner.Parse(context.TargetId.Key.ToString()!);
            if (owner.BrainId != BrainScope.CurrentId()) { throw new UnauthorizedAccessException("This storage belongs to another brain."); }
            var caller = CallerContextStamper.Require();
            if (!CallerContextStamper.IsTrusted(caller) || caller.Kind == IntoChat.OS.Framework.Modules.CallerKind.App)
            { throw new UnauthorizedAccessException("Only trusted host code may read app storage."); }
        }
        else if (contract == typeof(ITableIndex))
        {
            var expected = context.InterfaceMethod.Name is nameof(ITableIndex.Retire) or nameof(ITableIndex.Reopen) ? AppGrainType.Name : TableNeuron.GrainType;
            var source = SourceOf(context.SourceId);
            if (source is null || source.Value.Type != expected)
            { throw new UnauthorizedAccessException("Only the owning module may change table lifetime."); }
            var owner = TableOwner.Parse(context.TargetId.Key.ToString()!);
            if (owner.BrainId != BrainScope.CurrentId()) { throw new UnauthorizedAccessException("This storage belongs to another brain."); }
            if (context.InterfaceMethod.Name == nameof(ITableIndex.Register) && source.Value.Key != (string)context.Request.GetArgument(0)!)
            { throw new UnauthorizedAccessException("A table can only register itself."); }
        }
        else if (context.Request.GetArgumentCount() > 0 && context.Request.GetArgument(0) is RuntimeInvocation call
            && string.Equals(call.Contract, Lifetime, StringComparison.Ordinal))
        {
            var source = SourceOf(context.SourceId);
            if (source is null || source.Value.Type != TableIndexNeuron.GrainType)
            { throw new UnauthorizedAccessException("Only the owning module may change table lifetime."); }
            var ownerKey = call.Arguments is [var argument] && argument.ValueKind == JsonValueKind.String ? argument.GetString()! : "";
            var owner = TableOwner.Parse(ownerKey);
            if (owner.BrainId != BrainScope.CurrentId()) { throw new UnauthorizedAccessException("This storage belongs to another brain."); }
            if (source.Value.Key != ownerKey) { throw new UnauthorizedAccessException("This table belongs to another owner."); }
        }
        await context.Invoke();
    }

    // The calling grain's type and os key; a runtime host grain (neuron.host*, keyed by the neuron id "brain/type/key") stands
    // for the plain neuron it runs.
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
