using DigitalBrain;
using DigitalBrain.Limits;
using DigitalBrain.Neurons;
using IntoChat.OS.Connections;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace IntoChat.OS.Modules.Apps;

public static class PoliciesEndpoints
{
    public sealed record TextRequest(string? Text);

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var policies = BrainRoutes.Group(endpoints, "/policies").AddEndpointFilter(ModuleRouteGuard.Guard);

        // Promises are the person's: an app calling through the app proxy can neither propose nor retire one.
        policies.MapPost("", (TextRequest request, [FromServices] PolicyWriter writer, CancellationToken ct) => AppsEndpoints.Respond(async () =>
        {
            if (!AppsEndpoints.IsPerson()) { return Results.Json(new { error = "Only you can propose a promise." }, statusCode: 403); }
            ArgumentException.ThrowIfNullOrWhiteSpace(request?.Text);
            var outcome = await writer.Write(BrainScope.CurrentId(), request.Text, ct);
            return outcome.Status == ProposalStatus.Refused
                ? Results.UnprocessableEntity(outcome)
                : Results.Ok(outcome);
        }));

        policies.MapGet("", (INeuronProxies brain, CancellationToken ct) => AppsEndpoints.Respond(async ()
            => Results.Ok(await Policies(brain).List(ct))));

        policies.MapGet("/actions", (INeuronProxies brain, CancellationToken ct) => AppsEndpoints.Respond(async ()
            => Results.Ok(await Policies(brain).Actions(ct))));

        policies.MapDelete("/{id}", (string id, INeuronProxies brain, CancellationToken ct) => AppsEndpoints.Respond(async () =>
        {
            if (!AppsEndpoints.IsPerson()) { return Results.Json(new { error = "Only you can retire a promise." }, statusCode: 403); }
            ArgumentException.ThrowIfNullOrWhiteSpace(id);
            await Policies(brain).Retire(id, ct);
            return Results.Ok(new { retired = id });
        }));
    }

    private static IPolicies Policies(INeuronProxies brain)
        => brain.Get<IPolicies>(NeuronId.Parse(IPolicies.Key(BrainScope.CurrentId())));
}
