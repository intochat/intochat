using DigitalBrain.Framework;
using DigitalBrain.Framework.Contracts;

namespace DigitalBrain.Postgres.Tables;

public interface ITableDescriptor : IGrainWithStringKey
{
    Task<TableDescription> Describe(string owner);
}

[GenerateSerializer]
public sealed record TableDescription(
    [property: Id(0)] string AppId,
    [property: Id(1)] string TableId,
    [property: Id(2)] TablePlacement Placement,
    [property: Id(3)] AcceptedTable Table);

internal sealed partial class TableNeuron : ITableDescriptor
{
    public async Task<TableDescription> Describe(string owner)
    {
        var caller = CallerContextStamper.Require();
        if (!CallerContextStamper.IsTrusted(caller) || caller.Kind == CallerKind.App) { throw new UnauthorizedAccessException("This table belongs to another brain or app."); }
        var scope = TableOwner.Parse(owner);
        if (scope.BrainId != BrainScope.CurrentId() || state.State.Owner != owner)
        { throw new UnauthorizedAccessException("This table belongs to another brain or app."); }
        await Tables(owner).RequireOpen();
        var table = state.State.Accepted ?? throw new PostgresQueryException("This table has not finished being defined.");
        return new(scope.AppId, this.GetPrimaryKeyString(), state.State.Placement ?? TablePlacement.Shared, table);
    }
}
