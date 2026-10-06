using DigitalBrain;
using DigitalBrain.Enforcement;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Enforcement;
using DigitalBrain.Postgres.Tables;

namespace DigitalBrain.Postgres;

[PlatformOnly]
internal interface IPostgresTableResource : INeuron
{
    Task<PostgresAppTableResource> DescribeResource(string owner);
}

[GenerateSerializer]
internal sealed record PostgresAppTableResource(
    [property: Id(0)] string AppId,
    [property: Id(1)] string TableId,
    [property: Id(2)] string Origin,
    [property: Id(3)] PostgresTableDefinition Table);

internal sealed partial class PostgresTableNeuron : IPostgresTableResource
{
    public async Task<PostgresAppTableResource> DescribeResource(string owner)
    {
        var caller = CallerContextStamper.Require();
        if (!CallerContextStamper.IsTrusted(caller) || caller.Kind == CallerKind.App) { throw new UnauthorizedAccessException("This table belongs to another brain or app."); }
        var scope = TableOwner.Parse(owner);
        if (scope.BrainId != BrainScope.CurrentId() || state.State.Owner != owner)
        { throw new UnauthorizedAccessException("This table belongs to another brain or app."); }
        await Tables(owner).RequireOpen();
        var table = state.State.Accepted ?? throw new PostgresQueryException("This table has not finished being defined.");
        return new(scope.AppId, this.GetPrimaryKeyString(), PostgresCapacityKind.OriginOrPlatform(state.State.Origin), table);
    }
}
