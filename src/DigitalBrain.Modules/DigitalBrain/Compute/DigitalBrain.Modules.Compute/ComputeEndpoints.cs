using DigitalBrain.Framework;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace DigitalBrain.Compute;

internal static class ComputeEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/compute/summary", async (IDigitalBrain brain, CancellationToken ct) =>
        {
            var accountId = CallerContextStamper.Require().AccountId;
            var ledger = brain.Get<IAllowanceLedger>(accountId);
            var limits = await ledger.ReadLimitsAsync(ct);
            var settled = await ledger.ReadSettledAsync(cancellationToken: ct);
            return Results.Ok(new
            {
                // chargedCompute mirrors settledCompute while the Flutter shell still reads it.
                chargedCompute = settled,
                settledCompute = settled,
                reservedCompute = await ledger.ReadReservedAsync(cancellationToken: ct),
                limits.LimitCompute,
                limits.SpentCompute,
                limits.HardStopped,
            });
        });
        endpoints.MapGet("/compute/limits", async (IDigitalBrain brain, CancellationToken ct) =>
            Results.Ok(await brain.Get<IAllowanceLedger>(CallerContextStamper.Require().AccountId).ReadLimitsAsync(ct)));
    }
}
