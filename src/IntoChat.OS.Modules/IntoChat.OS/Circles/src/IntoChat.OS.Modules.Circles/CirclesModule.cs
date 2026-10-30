using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IntoChat.OS.Modules.Circles;

// Circles: a circle is a brain of its own (T26-1) whose members are accounts; it holds a conversation, the apps
// shared into it and a share of its creator's compute. A member's own brain creates and lists its circles.
// The circle, its chat and invites, the factory and the invite code index are plain neurons.
[DigitalBrain.Module("circles")]
public sealed partial class CirclesModule : DigitalBrain.Module, IHttpModule
{
    public static DigitalBrain.ModuleManifest Manifest { get; } = new(Provides: [], Requires: [], Needs: [], Settings: [], Secrets: []);

    public static System.Reflection.Assembly? Contracts { get; } = typeof(ICircle).Assembly;

    public void Configure(IEndpointRouteBuilder endpoints)
    {
        // Joining precedes membership. Only this invitation-checked operation is reachable here;
        // ordinary circle routes still require membership before entering the brain.
        endpoints.MapPost("/circle-invitations/{brainId}/accept", async (string brainId, HttpContext http, IServiceProvider plain) =>
        {
            if (http.User.Identity?.IsAuthenticated != true || !CallerContextStamper.TryGet(out var caller)
                || caller.Kind != CallerKind.User || caller.StampedBy != TrustedEdge.AuthenticatedHttp)
            { return Results.Unauthorized(); }
            if (!BrainScope.IsValidId(brainId)) { return Results.BadRequest(); }
            CallerContextStamper.Stamp(caller with { BrainId = brainId });
            try { return Results.Ok(await SiloNeurons.Get<ICircle>(plain, ICircle.Key(brainId)).Accept()); }
            catch (Exception error) when (error is UnauthorizedAccessException or InvalidOperationException)
            { return Results.StatusCode(StatusCodes.Status403Forbidden); }
            finally { CallerContextStamper.Stamp(caller); }
        });
        MapInviteCodes(endpoints);
    }

    public static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.TryAddSingleton<ICircleInviteRedeemer, CircleInviteRedeemer>();
        // One birth, reached as a creation handler and by the factory (which completes a birth that failed).
        if (services.Any(descriptor => descriptor.ServiceType == typeof(CircleBirth))) { return; }
        services.AddSingleton<CircleBirth>();
        services.AddSingleton<ICreationHandler>(provider => provider.GetRequiredService<CircleBirth>());
    }
}
