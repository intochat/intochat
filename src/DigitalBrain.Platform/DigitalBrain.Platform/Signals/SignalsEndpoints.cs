using System.Text.Json;
using DigitalBrain.Framework;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace DigitalBrain.Platform.Signals;

internal sealed record SignalView(long Sequence, string Type, string Publisher, JsonElement Payload);

internal sealed record SignalsPage(long Next, SignalView[] Signals);

// What the shell's replay scrub reads: the brain's journalled signals after a sequence number.
// Experimental (ruling R19): Sequence is re-ranked on every read across the part logs this silo has open, so a
// `since` cursor can skip or repeat signals. Every response says so until it pages by the per-part seq cursor.
internal static class SignalsEndpoints
{
    internal const string ExperimentalHeader = "X-DigitalBrain-Experimental";
    internal const string ExperimentalValue = "unstable-cursor";

    internal const int DefaultLimit = 200;
    internal const int MaximumLimit = 1000;

    internal static void MapSignals(this IEndpointRouteBuilder endpoints)
    {
        var group = BrainRoutes.Group(endpoints, "/signals");
        group.MapGet("", async (HttpResponse response, long? since, int? limit, [Microsoft.AspNetCore.Mvc.FromServices] ISignalJournal journal) =>
        {
            MarkExperimental(response);
            if (since is < 0 || limit is < 1 or > MaximumLimit) { return Results.BadRequest(new { error = $"since is 0 or more; limit is 1 to {MaximumLimit}." }); }
            return Results.Ok(await Read(journal, BrainScope.CurrentId(), since ?? 0, limit ?? DefaultLimit));
        });
    }

    internal static void MarkExperimental(HttpResponse response) => response.Headers[ExperimentalHeader] = ExperimentalValue;

    internal static async Task<SignalsPage> Read(ISignalJournal journal, string brainId, long since, int limit)
    {
        var read = await journal.ReadSince(brainId, since, limit);
        return new SignalsPage(read.Length == 0 ? since : read[^1].Sequence, [.. read.Select(j => new SignalView(
            j.Sequence, j.Signal.GetType().Name, j.Signal.Publisher.ToString(), JsonSerializer.SerializeToElement(j.Signal, j.Signal.GetType())))]);
    }
}
