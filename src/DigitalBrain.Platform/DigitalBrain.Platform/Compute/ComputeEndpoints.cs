using DigitalBrain.Framework;
using DigitalBrain.Framework.Compute;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace DigitalBrain.Platform.Compute;

internal sealed record CreditInput(long Micros);

// What the shell's Compute panel reads: the caller's balance and the brain's spend grouped one way.
internal static class ComputeEndpoints
{
    internal const int MaximumTake = 500;

    internal static void MapCompute(this IEndpointRouteBuilder endpoints)
    {
        var compute = BrainRoutes.Group(endpoints, "/compute");

        compute.MapGet("", async (string? by, DateTimeOffset? since, IGrainFactory grains) =>
        {
            var grouping = UsageGrouping.App;
            if (by is not null && !Enum.TryParse(by, ignoreCase: true, out grouping)) { return Results.BadRequest(new { error = $"Unknown grouping '{by}'." }); }
            var accountId = CallerContextStamper.Require().AccountId;
            var rows = await grains.GetGrain<IUsageGrain>(BrainScope.CurrentId())
                .Group(grouping, since ?? DateTimeOffset.UtcNow.AddDays(-30), MaximumTake);
            var balance = await grains.GetGrain<ILedgerGrain>(accountId).Balance();
            return Results.Ok(new CostSummary(balance, rows.Sum(row => row.Micros), rows));
        });

        compute.MapGet("/recent", async (int? take, IGrainFactory grains) =>
        {
            if (take is < 0 or > MaximumTake) { return Results.BadRequest(new { error = $"take is 0 to {MaximumTake}." }); }
            var recent = await grains.GetGrain<IUsageGrain>(BrainScope.CurrentId()).Recent(take ?? 50);
            return Results.Ok(recent.Select(cost => new
            {
                cost.TraceId, cost.CostId, Kind = cost.Kind.ToString().ToLowerInvariant(), cost.Item, cost.Quantity,
                cost.Unit, cost.Micros, cost.Synapse, cost.App, cost.Neuron, cost.At,
            }));
        });

        // A simulated top-up of the caller's own account until real payments exist.
        compute.MapPost("/credit", async (CreditInput input, IGrainFactory grains) =>
        {
            if (input.Micros <= 0) { return Results.BadRequest(new { error = "Credit a positive amount." }); }
            var accountId = CallerContextStamper.Require().AccountId;
            var ledger = grains.GetGrain<ILedgerGrain>(accountId);
            await ledger.Credit($"credit/{Guid.NewGuid():N}", input.Micros, "simulated top-up");
            return Results.Ok(new { balanceMicros = await ledger.Balance() });
        });
    }
}
