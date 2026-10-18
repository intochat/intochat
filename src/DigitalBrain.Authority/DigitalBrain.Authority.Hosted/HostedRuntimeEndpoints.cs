using System.Reflection;
using DigitalBrain.Framework;
using DigitalBrain.Authority.Hosting;
using DigitalBrain.Framework.Platform.Identity;
using DigitalBrain.Authority.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using DigitalBrain.Authority.Storage;

namespace DigitalBrain.Authority.Hosted;

internal static class HostedRuntimeEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var management = endpoints.MapGroup("/runtime/management/v1").AllowAnonymous();
        management.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            http.Response.Headers.CacheControl = "no-store";
            var authentication = http.RequestServices.GetRequiredService<RuntimeMachineAuthentication>();
            return await authentication.AuthenticateAsync(http, http.RequestAborted)
                ? await next(context) : Results.Unauthorized();
        });
        management.MapPost("/ensure", EnsureAsync);
        management.MapGet("/observation", ObserveAsync);
        HostedWorkspaceEndpoints.Map(endpoints, management);
    }

    private static async Task<IResult> EnsureAsync([FromServices] IOptions<HostedRuntimeOptions> options,
        [FromServices] IIdentityClient identity, [FromServices] BrainBirths births,
        [FromServices] TimeProvider clock, [FromServices] IServiceProvider services, CancellationToken ct)
    {
        var scope = options.Value;
        var existing = await identity.OwnerOfBrain(scope.BrainId).WaitAsync(ct);
        if (existing is not null && (existing.AccountId != scope.AccountId || existing.OwnerPrincipalId != scope.AccountId))
        {
            return Results.Conflict(new { errorCode = "RUNTIME_SCOPE_CONFLICT" });
        }
        await identity.EnsureAccount(new Account
        {
            AccountId = scope.AccountId,
            OwnerPrincipalId = scope.AccountId,
            Name = scope.AccountId,
            CreatedAt = clock.GetUtcNow()
        }).WaitAsync(ct);
        var provision = await identity.EnsureBrain(new Member
        {
            PrincipalId = scope.AccountId,
            AccountId = scope.AccountId,
            BrainId = scope.BrainId,
            Role = MemberRole.Owner,
            DisplayName = scope.BrainId,
            JoinedAt = clock.GetUtcNow()
        }).WaitAsync(ct);
        await births.Born(provision).WaitAsync(ct);
        return await ObserveAsync(options, identity, services, ct);
    }

    private static async Task<IResult> ObserveAsync([FromServices] IOptions<HostedRuntimeOptions> options,
        [FromServices] IIdentityClient identity, [FromServices] IServiceProvider services, CancellationToken ct)
    {
        var scope = options.Value;
        var owner = await identity.OwnerOfBrain(scope.BrainId).WaitAsync(ct);
        var registered = owner?.AccountId == scope.AccountId && owner.OwnerPrincipalId == scope.AccountId &&
            await identity.BrainExists(BrainScope.Create(scope.AccountId, scope.BrainId).Id).WaitAsync(ct);
        var version = typeof(HostedRuntimeEndpoints).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        var logs = services.GetService<BrainLogs>();
        var platform = services.GetKeyedService<Orleans.Storage.IGrainStorage>(DigitalBrainNames.PlatformGrainStorage) as HostedPlatformGrainStorage;
        var storageAttached = registered && logs is not null && services.GetService<IReminderTable>() is HostedReminderTable &&
            platform?.Scope == BrainScope.Create(scope.AccountId, scope.BrainId).Id &&
            await logs.CheckStorageAsync(BrainScope.Create(scope.AccountId, scope.BrainId).Id, ct);
        var lifetime = services.GetService<IHostApplicationLifetime>();
        var healthy = registered && storageAttached && lifetime?.ApplicationStarted.IsCancellationRequested == true &&
            !lifetime.ApplicationStopping.IsCancellationRequested;
        return Results.Ok(new HostedRuntimeObservation(1, version, scope.AccountId, scope.BrainId, scope.RuntimeId, registered,
            healthy, storageAttached));
    }
}
