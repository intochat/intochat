using DigitalBrain.Brains;
using DigitalBrain.Capabilities;
using DigitalBrain.Log;
using DigitalBrain.Modules;
using DigitalBrain.Runtime.Http.Surface;
using DigitalBrain.Access;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Framework;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using IntoChat.OS.Host.Logging;

namespace IntoChat.OS.Modules.Circles;

// Circles: a circle is a brain of its own (T26-1) whose members are accounts; it holds a conversation, the apps
// shared into it and a share of its creator's compute. A member's own brain creates and lists its circles.
// The circle, its chat and invites, the factory and the invite code index are plain neurons.
[DigitalBrain.Modules.Module("circles")]
public sealed partial class CirclesModule : DigitalBrain.Modules.Module, IHttpModule
{
    public static DigitalBrain.Modules.ModuleManifest Manifest { get; } = new(Provides: [], Requires: [], Needs: [], Settings: [], Secrets: []);

    public static System.Reflection.Assembly? Contracts { get; } = typeof(ICircle).Assembly;

    public void Configure(IEndpointRouteBuilder endpoints)
    {
        // Joining precedes membership. Only this invitation-checked operation is reachable here;
        // ordinary circle routes still require membership before entering the brain.
        endpoints.MapPost("/circle-invitations/{brainId}/accept", async (string brainId, HttpContext http, IServiceProvider plain) =>
        {
            var edge = plain.GetRequiredService<EdgeAuthority>();
            if (http.User.Identity?.IsAuthenticated != true || HttpCaller.Current(http) is not { Actor: Actor.Person person, Direct: true } caller)
            { return Results.Unauthorized(); }
            if (!BrainScope.IsValidId(brainId)) { return Results.BadRequest(); }
            using var there = edge.Enter(edge.Person(person.Principal, person.Account, new DigitalBrain.BrainId(brainId), caller.Conversation));
            try { return Results.Ok(await SiloNeurons.Get<ICircle>(plain, ICircle.Key(brainId)).Accept()); }
            catch (Exception error) when (error is UnauthorizedAccessException or InvalidOperationException)
            {
                (plain.GetService<ILoggerFactory>()?.CreateLogger<CirclesModule>() ?? OsConsoleLog.For<CirclesModule>())
                    .LogSwallowed(error, "accepting circle invitation");
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }
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
        services.AddSingleton<DigitalBrain.Brains.ICreationHandler>(provider => provider.GetRequiredService<CircleBirth>());
    }
}
