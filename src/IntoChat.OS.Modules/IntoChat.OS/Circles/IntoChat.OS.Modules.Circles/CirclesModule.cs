using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IntoChat.OS.Modules.Circles;

// Circles: a circle is a brain of its own (T26-1) whose members are accounts; it holds a conversation, the apps
// shared into it and a share of its creator's compute. A member's own brain creates and lists its circles.
public sealed partial class CirclesModule() : Module(new ModuleId("circles")), IHttpModule
{
    // R4-04: what the module needs from its host (CirclesNeurons carries it).
    public static DigitalBrain.ModuleManifest Manifest => CirclesNeurons.Manifest;

    public override System.Reflection.Assembly? Contracts => typeof(ICircle).Assembly;

    // 06-08: the circle, its chat and invites, the factory and the invite code index are plain neurons.
    public override Type? Neurons => typeof(CirclesNeurons);

    public void Configure(IEndpointRouteBuilder endpoints)
    {
        // Joining precedes membership. Only this invitation-checked operation is reachable here;
        // ordinary circle routes still require membership before entering the brain.
        endpoints.MapPost("/circle-invitations/{brainId}/accept", async (string brainId, HttpContext http, IntoChat.OS.Runtime.IPlainNeurons plain) =>
        {
            if (http.User.Identity?.IsAuthenticated != true || !CallerContextStamper.TryGet(out var caller)
                || caller.Kind != CallerKind.User || caller.StampedBy != TrustedEdge.AuthenticatedHttp)
            { return Results.Unauthorized(); }
            if (!BrainScope.IsValidId(brainId)) { return Results.BadRequest(); }
            CallerContextStamper.Stamp(caller with { BrainId = brainId });
            try { return Results.Ok(await plain.Get<ICircle>(ICircle.Key(brainId)).Accept()); }
            catch (Exception error) when (error is UnauthorizedAccessException or InvalidOperationException)
            { return Results.StatusCode(StatusCodes.Status403Forbidden); }
            finally { CallerContextStamper.Stamp(caller); }
        });
        MapInviteCodes(endpoints);
    }

    public override void Configure(ISiloBuilder silo)
    {
        ArgumentNullException.ThrowIfNull(silo);
        silo.Services.TryAddSingleton<ICircleInviteRedeemer, CircleInviteRedeemer>();
        // One birth, reached as a creation handler and by the factory (which completes a birth that failed).
        if (silo.Services.Any(descriptor => descriptor.ServiceType == typeof(CircleBirth))) { return; }
        silo.Services.AddSingleton<CircleBirth>();
        silo.Services.AddSingleton<ICreationHandler>(provider => provider.GetRequiredService<CircleBirth>());
    }
}

// The DigitalBrain module of the Circles module's plain neurons (06-08).
[DigitalBrain.Module("circles")]
public sealed class CirclesNeurons : DigitalBrain.Module
{
    public static DigitalBrain.ModuleManifest Manifest { get; } = new(Provides: [], Requires: [], Needs: [], Settings: [], Secrets: []);
}
