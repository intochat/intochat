using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Sdk;
using IntoChat.OS.Sdk.Modules;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Circles;

// Circles: a circle is a brain of its own (T26-1) whose members are accounts; it holds a conversation, the apps
// shared into it and a share of its creator's compute. A member's own brain creates and lists its circles.
public sealed class CirclesModule() : Module(new ModuleId("circles")), IHttpModule
{
    public override System.Reflection.Assembly? Contracts => typeof(ICircle).Assembly;

    public void Configure(IEndpointRouteBuilder endpoints)
    {
        // Joining precedes membership. Only this invitation-checked operation is reachable here;
        // ordinary circle routes still require membership before entering the brain.
        endpoints.MapPost("/circle-invitations/{brainId}/accept", async (string brainId, HttpContext http, IGrainFactory grains) =>
        {
            if (http.User.Identity?.IsAuthenticated != true || !CallerContextStamper.TryGet(out var caller)
                || caller.Kind != CallerKind.User || caller.StampedBy != TrustedEdge.AuthenticatedHttp)
            { return Results.Unauthorized(); }
            if (!BrainScope.IsValidId(brainId)) { return Results.BadRequest(); }
            CallerContextStamper.Stamp(caller with { BrainId = brainId });
            try { return Results.Ok(await grains.GetGrain<ICircle>(ICircle.Key(brainId)).Accept()); }
            catch (Exception error) when (error is UnauthorizedAccessException or InvalidOperationException)
            { return Results.StatusCode(StatusCodes.Status403Forbidden); }
            finally { CallerContextStamper.Stamp(caller); }
        });
    }

    public override void Configure(ISiloBuilder silo)
    {
        ArgumentNullException.ThrowIfNull(silo);
        // One birth, reached as a creation handler and by the factory (which completes a birth that failed).
        if (silo.Services.Any(descriptor => descriptor.ServiceType == typeof(CircleBirth))) { return; }
        silo.Services.AddSingleton<CircleBirth>();
        silo.Services.AddSingleton<ICreationHandler>(provider => provider.GetRequiredService<CircleBirth>());
    }
}
