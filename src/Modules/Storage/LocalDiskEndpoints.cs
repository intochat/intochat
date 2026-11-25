using DigitalBrain.Log;
using DigitalBrain.Runtime.Http.Surface;
using DigitalBrain.Runtime.Orleans.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Host.Primitives;

namespace IntoChat.OS.Modules.LocalDisk;

internal sealed record LocalDiskRootInput(string? Path);

internal static class LocalDiskEndpoints
{
    internal static void Map(IEndpointRouteBuilder endpoints)
    {
        var group = BrainRoutes.Group(endpoints, "/storage/local");
        group.MapGet("/roots", (string brainId, IServiceProvider services) => Roots(brainId, services));
        group.MapPost("/roots/allow", (string brainId, LocalDiskRootInput input, HttpContext http) => Change(brainId, input, http, allow: true));
        group.MapPost("/roots/revoke", (string brainId, LocalDiskRootInput input, HttpContext http) => Change(brainId, input, http, allow: false));
    }

    private static async Task<IResult> Roots(string brainId, IServiceProvider services)
    {
        if (!ValidateBrainId(brainId)) { return Results.BadRequest(new { error = "Invalid brain id." }); }
        return Results.Ok(await Neuron(services, brainId).Roots().ConfigureAwait(false));
    }

    private static async Task<IResult> Change(string brainId, LocalDiskRootInput input, HttpContext http, bool allow)
    {
        if (!ValidateBrainId(brainId)) { return Results.BadRequest(new { error = "Invalid brain id." }); }
        if (LocalDiskRoots.Syntax(input.Path) is { } syntax) { return Results.BadRequest(new { error = syntax }); }
        // The group admitted the session's person as a member of this brain; the local disk takes the change from that person
        // acting in this brain, as a gRPC call is stamped (BrainCall).
        if (HttpCaller.Current(http) is not { Actor: DigitalBrain.Access.Actor.Person person }) { return Results.Unauthorized(); }
        var services = http.RequestServices;
        var edge = services.GetRequiredService<DigitalBrain.Access.EdgeAuthority>();
        using var entered = edge.Enter(edge.Person(person.Principal, person.Account, new DigitalBrain.BrainId(brainId)));
        try
        {
            var neuron = Neuron(services, brainId);
            if (allow) { await neuron.AllowRoot(input.Path!).ConfigureAwait(false); }
            else { await neuron.RevokeRoot(input.Path!).ConfigureAwait(false); }
            return Results.Ok(await neuron.Roots().ConfigureAwait(false));
        }
        catch (Exception error) when (LocalDiskRoots.FailureText(error) is { } text)
        {
            OsConsoleLog.For(typeof(LocalDiskEndpoints)).LogSwallowed(error, "updating local disk root");
            return Results.BadRequest(new { error = text });
        }
    }

    private static bool ValidateBrainId(string? brainId)
        => !string.IsNullOrWhiteSpace(brainId)
           && SafeRelativePath.TryParse(brainId, out var safe)
           && !safe.Value.Contains('/');

    private static ILocalDisk Neuron(IServiceProvider services, string brainId)
        => SiloNeurons.Get<ILocalDisk>(services, new DigitalBrain.NeuronId(new DigitalBrain.BrainId(brainId), LocalDiskNeuron.TypeName, LocalDiskNeuron.KeyName));
}
