using DigitalBrain.Framework;

namespace DigitalBrain.Postgres.Tables;

// Every index and lifetime call names its owner; the guard checks the owner is this brain's and
// the source is the one grain type allowed to make that call.
internal sealed class TableLifecycleGuard : IIncomingGrainCallFilter
{
    public async Task Invoke(IIncomingGrainCallContext context)
    {
        var contract = context.InterfaceMethod.DeclaringType;
        if (contract == typeof(ITableIndex) && context.InterfaceMethod.Name == nameof(ITableIndex.ReadTables))
        {
            var owner = TableOwner.Parse(context.TargetId.Key.ToString()!);
            if (owner.BrainId != BrainScope.CurrentId()) { throw new UnauthorizedAccessException("This storage belongs to another brain."); }
            var caller = CallerContextStamper.Require();
            if (!CallerContextStamper.IsTrusted(caller) || caller.Kind == DigitalBrain.Framework.CallerKind.App)
            { throw new UnauthorizedAccessException("Only trusted host code may read app storage."); }
        }
        else if (contract == typeof(ITableIndex) || contract == typeof(ITableLifetime))
        {
            var source = context.SourceId;
            var expected = contract == typeof(ITableLifetime) ? TableIndexNeuron.GrainType
                : context.InterfaceMethod.Name is nameof(ITableIndex.Retire) or nameof(ITableIndex.Reopen) ? AppGrainType.Name : TableNeuron.GrainType;
            if (source is null || source.Value.Type.ToString() != expected)
            { throw new UnauthorizedAccessException("Only the owning module may change table lifetime."); }
            var ownerKey = contract == typeof(ITableIndex) ? context.TargetId.Key.ToString()! : (string)context.Request.GetArgument(0)!;
            var owner = TableOwner.Parse(ownerKey);
            if (owner.BrainId != BrainScope.CurrentId()) { throw new UnauthorizedAccessException("This storage belongs to another brain."); }
            if (contract == typeof(ITableLifetime) && source.Value.Key.ToString() != ownerKey)
            { throw new UnauthorizedAccessException("This table belongs to another owner."); }
            if (contract == typeof(ITableIndex) && context.InterfaceMethod.Name == nameof(ITableIndex.Register)
                && source.Value.Key.ToString() != (string)context.Request.GetArgument(0)!)
            { throw new UnauthorizedAccessException("A table can only register itself."); }
        }
        await context.Invoke();
    }
}
