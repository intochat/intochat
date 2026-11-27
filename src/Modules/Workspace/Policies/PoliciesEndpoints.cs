using DigitalBrain;
using DigitalBrain.Limits;
using DigitalBrain.Neurons;
using IntoChat.OS.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using DigitalBrain.Runtime.Http;
using DigitalBrain.Runtime.Http.Surface;

namespace IntoChat.OS.Modules.Apps;

internal static class PoliciesEndpoints
{
    internal sealed record TextRequest(string? Text);

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var policies = BrainRouteGroups.Brain(endpoints, "/policies").AddEndpointFilter(ModuleRouteGuard.Guard);

        // Promises are the person's: an app calling through the app proxy can neither propose nor retire one.
        policies.MapPost("", (HttpContext http, TextRequest request, [FromServices] PolicyWriter writer, CancellationToken ct) => AppsEndpoints.Respond(async () =>
        {
            if (!AppsEndpoints.IsPerson(HttpCaller.Current(http))) { return Results.Json(new WorkspaceErrorResponse("Only you can propose a promise."), WorkspaceAppsJsonContext.Default.WorkspaceErrorResponse, statusCode: StatusCodes.Status403Forbidden); }
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

        policies.MapDelete("/{id}", (HttpContext http, string id, INeuronProxies brain, CancellationToken ct) => AppsEndpoints.Respond(async () =>
        {
            if (!AppsEndpoints.IsPerson(HttpCaller.Current(http))) { return Results.Json(new WorkspaceErrorResponse("Only you can retire a promise."), WorkspaceAppsJsonContext.Default.WorkspaceErrorResponse, statusCode: StatusCodes.Status403Forbidden); }
            ArgumentException.ThrowIfNullOrWhiteSpace(id);
            await Policies(brain).Retire(id, ct);
            return Results.Json(new PolicyRetiredResponse(id), WorkspaceAppsJsonContext.Default.PolicyRetiredResponse);
        }));
    }

    private static IPolicies Policies(INeuronProxies brain)
        => brain.Get<IPolicies>(NeuronId.Parse(IPolicies.Key(BrainScope.CurrentId())));
}
