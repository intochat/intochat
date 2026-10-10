using DigitalBrain.Framework;
using DigitalBrain.Framework.Parts;
using DigitalBrain.Platform.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Platform.Parts;

// A machine's own handle on the parts it holds (R40): release one so another machine can take it, and read what
// it stores. Only the account that owns the part's brain may do either; reading never acquires a part.
internal static class MachinePartEndpoints
{
    public static void Map(IEndpointRouteBuilder routes)
    {
        routes.MapPost("/machine/parts/{part}/release", ReleaseAsync);
        routes.MapGet("/machine/parts/{part}/records/{key}", RecordsAsync);
    }

    private static async Task<IResult> ReleaseAsync(string part, HttpContext http, CancellationToken ct)
    {
        if (http.RequestServices.GetService<PartActivations>() is not { } activations) { return Results.NotFound(); }
        if (await Refusal(http, part) is { } refused) { return refused; }
        await activations.Release(Parse(part)).WaitAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> RecordsAsync(string part, string key, HttpContext http, CancellationToken ct)
    {
        if (http.RequestServices.GetService<PartLogs>() is not { } logs) { return Results.NotFound(); }
        if (await Refusal(http, part) is { } refused) { return refused; }
        var id = Parse(part);
        if (!logs.Opened.Contains(id)) { return Results.Conflict(new { error = $"This machine does not hold part {id}." }); }
        var log = await logs.For(id).WaitAsync(ct);
        var wanted = Uri.UnescapeDataString(key);
        var records = log.Records()
            .Where(record => GrainId.TryParse(record.GrainId, out var grain) && grain.Key.ToString() == wanted)
            .Select(record => new
            {
                record.GrainId,
                record.StateName,
                record.Record.ETagSeq,
                // The grain serializer's bytes: a record is written typed, so only its grain can read it back.
                Record = Convert.ToBase64String(record.Record.Record),
            })
            .ToArray();
        return Results.Ok(records);
    }

    // 401 without a stamped caller, 403 unless the caller's account owns the part's brain.
    private static async Task<IResult?> Refusal(HttpContext http, string part)
    {
        if (!CallerContextStamper.TryGet(out var caller)) { return Results.Unauthorized(); }
        PartId id;
        try { id = Parse(part); }
        catch (ArgumentException) { return Results.BadRequest(); }
        var owner = await http.RequestServices.GetRequiredService<IKeyringClient>().OwnerOf(id.BrainId);
        return owner is not null && owner == caller.AccountId ? null : Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    internal static PartId Parse(string part)
    {
        var value = Uri.UnescapeDataString(part ?? "");
        return value.EndsWith("/core", StringComparison.Ordinal) ? PartId.Core(value[..^"/core".Length]) : PartId.Of(value);
    }
}
