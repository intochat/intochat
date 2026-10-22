using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Compute;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace IntoChat.OS.Authority.Compute;

internal sealed record CreditInput(long Micros);

// What the shell's Compute panel reads: the caller's balance and the brain's spend grouped one way.
internal static class ComputeEndpoints
{
    internal const int MaximumTake = 500;

    internal static void MapCompute(this IEndpointRouteBuilder endpoints)
    {
        var compute = BrainRoutes.Group(endpoints, "/compute");

        compute.MapGet("", async (string? by, DateTimeOffset? since, AuthorityNeurons neurons) =>
        {
            var grouping = UsageGrouping.App;
            if (by is not null && !Enum.TryParse(by, ignoreCase: true, out grouping)) { return Results.BadRequest(new { error = $"Unknown grouping '{by}'." }); }
            var accountId = CallerContextStamper.Require().AccountId;
            var rows = await neurons.Usage(BrainScope.CurrentId())
                .Group(grouping, since ?? DateTimeOffset.UtcNow.AddDays(-30), MaximumTake);
            var balance = await neurons.Ledger(accountId).Balance();
            return Results.Ok(new CostSummary(balance, rows.Sum(row => row.Micros), rows)
            {
                KnownListPriceMicros = rows.Sum(row => row.KnownListPriceMicros ?? 0),
                UnpricedCount = rows.Sum(row => row.UnpricedCount ?? 0),
                PendingCount = rows.Sum(row => row.PendingCount ?? 0),
                UnknownCount = rows.Sum(row => row.UnknownCount ?? row.Count),
            });
        });

        compute.MapGet("/recent", async (int? take, AuthorityNeurons neurons) =>
        {
            if (take is < 0 or > MaximumTake) { return Results.BadRequest(new { error = $"take is 0 to {MaximumTake}." }); }
            var recent = await neurons.Usage(BrainScope.CurrentId()).Recent(take ?? 50);
            return Results.Ok(recent.Select(View));
        });

        compute.MapGet("/traces/{traceId}", async (string traceId, int? take, string? continuationToken, AuthorityNeurons neurons) =>
        {
            if (take is < 1 or > MaximumTake) { return Results.BadRequest(new { error = $"take is 1 to {MaximumTake}." }); }
            try
            {
                var page = await neurons.Compute(BrainScope.CurrentId()).ReadTrace(traceId, take ?? 100, continuationToken);
                return Results.Ok(new
                {
                    Rows = page.Rows.Select(View),
                    page.ChargedMicros,
                    page.KnownListPriceMicros,
                    page.UnpricedCount,
                    page.PendingCount,
                    page.UnknownCount,
                    page.SnapshotWatermark,
                    page.ContinuationToken,
                    Status = page.Status.ToString().ToLowerInvariant(),
                });
            }
            catch (ArgumentException) { return Results.BadRequest(new { error = "Invalid trace selector or continuation." }); }
        });

        // A simulated top-up of the caller's own account until real payments exist.
        compute.MapPost("/credit", async (CreditInput input, AuthorityNeurons neurons) =>
        {
            if (input.Micros <= 0) { return Results.BadRequest(new { error = "Credit a positive amount." }); }
            var accountId = CallerContextStamper.Require().AccountId;
            var ledger = neurons.Ledger(accountId);
            await ledger.Credit($"credit/{Guid.NewGuid():N}", input.Micros, "simulated top-up");
            return Results.Ok(new { balanceMicros = await ledger.Balance() });
        });
    }

    private static object View(Cost cost) => new
    {
        cost.TraceId,
        cost.CostId,
        Kind = cost.Kind.ToString().ToLowerInvariant(),
        cost.Item,
        cost.Quantity,
        cost.Unit,
        cost.Micros,
        cost.Synapse,
        cost.App,
        cost.Neuron,
        cost.At,
        cost.ListPriceMicros,
        cost.EstimatedListPriceMicros,
        PricingStatus = cost.PricingStatus?.ToString().ToLowerInvariant(),
        cost.TariffVersion,
        FundingKind = cost.FundingKind?.ToString().ToLowerInvariant(),
        Outcome = cost.Outcome?.ToString().ToLowerInvariant(),
        cost.InvocationId,
        AttemptState = cost.AttemptState?.ToString().ToLowerInvariant(),
        cost.ProviderRequestId,
        ChargePolicy = cost.ChargePolicy?.ToString().ToLowerInvariant(),
    };
}
