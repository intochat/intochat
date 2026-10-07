using System.Text.Json;
using System.Text.Json.Nodes;
using DigitalBrain.Framework;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace DigitalBrain.Compute.Usage;

// A brain's usage history for the shell: each row as it was recorded, with what the ledger settled
// and still holds for it.
internal static class ComputeUsageEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var compute = BrainRoutes.Group(endpoints, "/compute");
        compute.MapGet("/usage", async (int? limit, string? cursor, IDigitalBrain brain, CancellationToken ct) =>
        {
            var accountId = CallerContextStamper.Require().AccountId;
            var scope = BrainScope.CurrentId();
            UsagePage page;
            try { page = await brain.Get<IComputeUsage>(accountId).Read(scope, limit ?? 20, cursor, ct); }
            catch (ArgumentException) { return Results.BadRequest(new { error = "Invalid usage limit or cursor." }); }
            var ledger = brain.Get<IAllowanceLedger>(accountId);
            var items = new List<JsonObject>();
            foreach (var row in page.Items)
            {
                var item = new JsonObject();
                foreach (var (name, value) in JsonNode.Parse(row.Payload)!.AsObject())
                { item[JsonNamingPolicy.CamelCase.ConvertName(name)] = value?.DeepClone(); }
                var id = item["id"]?.GetValue<string>() ?? "";
                var settled = await ledger.ReadSettledAsync(scope, id, ct);
                item["occurredAt"] = row.OccurredAt;
                // chargedCompute mirrors settledCompute while the Flutter shell still reads it.
                item["chargedCompute"] = settled;
                item["settledCompute"] = settled;
                item["reservedCompute"] = await ledger.ReadReservedAsync(scope, id, ct);
                items.Add(item);
            }
            return Results.Ok(new { items, page.NextCursor });
        });
    }
}
