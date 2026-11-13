using DigitalBrain.Brains;
using DigitalBrain.Capabilities.Connections;
using DigitalBrain.Capabilities.Connections.Connectors;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Authority.Identity;
using IntoChat.OS.Connections;
using IntoChat.OS.Connections.Identity;
using IntoChat.OS.Connections.Platform.Identity;
using IntoChat.OS.Framework;
using IntoChat.Sdk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Modules.Microsoft.Windows;

internal sealed record WindowsRootInput(string? Path);

internal sealed record WindowsFolderGrantInput(
    string? AppKey,
    string? Slot,
    string? HostPath,
    FolderAccess Access = FolderAccess.Read,
    bool Enabled = true);

internal static class WindowsEndpoints
{
    internal static void Map(IEndpointRouteBuilder endpoints)
    {
        var group = BrainRoutes.Group(endpoints, "/windows");
        group.MapGet("/device", ([FromServices] MachineIdentity identity) =>
            OperatingSystem.IsWindows()
                ? Results.Ok(new { machineId = identity.Id, machineName = Environment.MachineName })
                : Results.NotFound());
        group.MapGet("/{machineId}/roots", (string machineId, [FromServices] MachineIdentity identity, [FromServices] IServiceProvider grains) =>
            WithMachine(machineId, identity, grains, async machine => Results.Ok(await machine.Roots())));
        group.MapPost("/{machineId}/roots/allow", (string machineId, WindowsRootInput input, [FromServices] MachineIdentity identity, [FromServices] IServiceProvider grains) =>
            ChangeRoot(machineId, input, identity, grains, true));
        group.MapPost("/{machineId}/roots/revoke", (string machineId, WindowsRootInput input, [FromServices] MachineIdentity identity, [FromServices] IServiceProvider grains) =>
            ChangeRoot(machineId, input, identity, grains, false));
        group.MapGet("/folders", GetFoldersAsync);
        group.MapPost("/folders/grants", GrantFolderAsync);
        group.MapDelete("/folders/grants/{appKey}", RevokeFolderGrantsAsync);
    }

    private static async Task<IResult> GetFoldersAsync(
        [FromServices] MachineIdentity identity,
        [FromServices] IOptions<WindowsOptions> options,
        [FromServices] IServiceProvider grains,
        LocalWindowsDeviceBootstrap? bootstrap = null,
        CancellationToken cancellationToken = default)
    {
        var brainId = BrainScope.CurrentId();
        if (OperatingSystem.IsWindows() && options.Value.AutoEnrollThisPc && bootstrap is not null)
        {
            await bootstrap.EnsureEnrolledAsync(brainId, cancellationToken).ConfigureAwait(false);
        }

        var gateway = SiloNeurons.Get<IWindowsFolderGateway>(grains, IWindowsFolderGateway.Key(brainId));
        var grants = await gateway.ListGrantsAsync(cancellationToken).ConfigureAwait(false);

        var connected = false;
        if (OperatingSystem.IsWindows())
        {
            var key = ConnectionKeys.For(brainId, WindowsModule.ManifestName, identity.Id);
            var view = await SiloNeurons.Get<IConnection>(grains, ConnectionKeys.IdFor(key)).Read().ConfigureAwait(false);
            connected = view.Status == ConnectionStatus.Connected;
        }

        return Results.Ok(new
        {
            grants,
            thisPc = new
            {
                supported = OperatingSystem.IsWindows(),
                machineId = OperatingSystem.IsWindows() ? identity.Id : null,
                machineName = OperatingSystem.IsWindows() ? Environment.MachineName : null,
                connected,
            },
            defaultDownloadsPath = OperatingSystem.IsWindows() ? options.Value.ResolveDefaultDownloadsPath() : null,
        });
    }

    private static async Task<IResult> GrantFolderAsync(
        WindowsFolderGrantInput input,
        [FromServices] IServiceProvider grains,
        [FromServices] IIdentityClient? identityClient = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(input.AppKey) ||
            string.IsNullOrWhiteSpace(input.Slot) ||
            string.IsNullOrWhiteSpace(input.HostPath))
        {
            return Results.BadRequest(new { error = "AppKey, Slot, and HostPath are required." });
        }

        var brainId = BrainScope.CurrentId();
        var gateway = SiloNeurons.Get<IWindowsFolderGateway>(grains, IWindowsFolderGateway.Key(brainId));
        try
        {
            var grant = new WindowsFolderGrant(input.AppKey.Trim(), input.Slot.Trim(), input.HostPath.Trim(), input.Access, input.Enabled);
            await gateway.GrantSlotAsync(grant, cancellationToken).ConfigureAwait(false);
            var grants = await gateway.ListGrantsAsync(cancellationToken).ConfigureAwait(false);
            var hasEnabledGrant = Array.Exists(
                grants,
                entry => entry.Enabled && string.Equals(entry.AppKey, grant.AppKey, StringComparison.OrdinalIgnoreCase));
            if (identityClient is not null)
            {
                var caller = ResolveOwnerCaller(brainId);
                if (hasEnabledGrant)
                {
                    var tuple = Grant.Tuple($"brain:{brainId}", $"app:{grant.AppKey}", FolderTarget(brainId), GrantRelations.Run) with
                    {
                        Id = FolderAuthorityGrantId(brainId, grant.AppKey),
                    };
                    await identityClient.GrantAsOwner(caller, tuple).WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await identityClient.RevokeAsOwner(caller, FolderAuthorityGrantId(brainId, grant.AppKey)).WaitAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            return Results.Ok(grants);
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return Results.BadRequest(new { error = error.Message });
        }
    }

    private static async Task<IResult> RevokeFolderGrantsAsync(
        string appKey,
        [FromServices] IServiceProvider grains,
        [FromServices] IIdentityClient? identityClient = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(appKey))
        {
            return Results.BadRequest(new { error = "AppKey is required." });
        }

        var brainId = BrainScope.CurrentId();
        var gateway = SiloNeurons.Get<IWindowsFolderGateway>(grains, IWindowsFolderGateway.Key(brainId));
        await gateway.RevokeAppAsync(appKey, cancellationToken).ConfigureAwait(false);
        if (identityClient is not null)
        {
            await identityClient.RevokeAsOwner(ResolveOwnerCaller(brainId), FolderAuthorityGrantId(brainId, appKey)).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        return Results.Ok(await gateway.ListGrantsAsync(cancellationToken).ConfigureAwait(false));
    }

    private static DigitalBrain.Capabilities.CapabilityCaller ResolveOwnerCaller(string brainId)
        => RuntimeCallerContext.Require() with { BrainId = brainId };

    private static string FolderAuthorityGrantId(string brainId, string appKey)
        => $"windows-folder:{brainId}:{appKey.Trim().ToLowerInvariant()}";

    private static string FolderTarget(string brainId)
        => $"{IWindowsFolderGateway.TypeName}/{brainId}/{IWindowsFolderGateway.DefaultKey}";

    private static Task<IResult> ChangeRoot(string machineId, WindowsRootInput input, MachineIdentity identity, [FromServices] IServiceProvider grains, bool allow)
        => WithMachine(machineId, identity, grains, async machine =>
        {
            if (string.IsNullOrWhiteSpace(input.Path) || !Path.IsPathFullyQualified(input.Path))
            { return Results.BadRequest(new { error = "Enter a full folder path on the connected Windows computer." }); }
            try
            {
                if (allow) { await machine.AllowRoot(input.Path); }
                else { await machine.RevokeRoot(input.Path); }
                return Results.Ok(await machine.Roots());
            }
            catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException)
            { return Results.BadRequest(new { error = "The folder is unavailable on the connected Windows computer." }); }
        });

    private static async Task<IResult> WithMachine(string machineId, MachineIdentity identity, [FromServices] IServiceProvider grains, Func<IWindows, Task<IResult>> action)
    {
        if (!OperatingSystem.IsWindows() || machineId != identity.Id) { return Results.NotFound(); }
        var key = ConnectionKeys.For(BrainScope.CurrentId(), "windows", machineId);
        if ((await SiloNeurons.Get<IConnection>(grains, ConnectionKeys.IdFor(key)).Read()).Status != ConnectionStatus.Connected)
        { return Results.Conflict(new { error = "Connect this Windows computer before choosing allowed folders." }); }
        return await action(SiloNeurons.Get<IWindows>(grains, key));
    }
}
