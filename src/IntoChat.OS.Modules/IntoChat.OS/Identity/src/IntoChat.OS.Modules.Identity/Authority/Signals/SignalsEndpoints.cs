using DigitalBrain.Brains;
using System.Text.Json;
using IntoChat.OS.Connections;
using IntoChat.OS.Framework;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace IntoChat.OS.Authority.Signals;

// At, TraceId and CausationId come from the journal entry; the older members are unchanged.
public sealed record SignalView(long Sequence, string Type, string Publisher, JsonElement Payload, DateTimeOffset At = default, string? TraceId = null, string? CausationId = null);

public sealed record SignalsPage(long Next, SignalView[] Signals);

// What the shell's replay scrub reads: the brain's journalled signals after a sequence number.
// The single local journal assigns durable monotonically increasing signal cursors.
internal static class SignalsEndpoints
{
    internal const int DefaultLimit = 200;
    internal const int MaximumLimit = 1000;

    internal static void MapSignals(this IEndpointRouteBuilder endpoints)
    {
        var group = BrainRoutes.Group(endpoints, "/signals");
        group.MapGet("", async (long? since, int? limit, [Microsoft.AspNetCore.Mvc.FromServices] ISignalJournal journal) =>
        {
            if (since is < 0 || limit is < 1 or > MaximumLimit) { return Results.BadRequest(new { error = $"since is 0 or more; limit is 1 to {MaximumLimit}." }); }
            return Results.Ok(await Read(journal, BrainScope.CurrentId(), since ?? 0, limit ?? DefaultLimit));
        });
    }

    internal static async Task<SignalsPage> Read(ISignalJournal journal, string brainId, long since, int limit)
    {
        var read = await journal.ReadSince(brainId, since, limit);
        return new SignalsPage(read.Length == 0 ? since : read[^1].Sequence, [.. read.Select(j => new SignalView(
            j.Sequence, j.Signal.GetType().Name, j.Publisher, JsonSerializer.SerializeToElement(j.Signal, j.Signal.GetType()), j.At, j.TraceId, j.CausationId))]);
    }
}
