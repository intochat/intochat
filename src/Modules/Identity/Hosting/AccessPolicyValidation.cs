using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans;
using IntoChat.OS.Modules.Identity.Accounts;
using IntoChat.OS.Modules.Identity.Grants;
using IntoChat.OS.Modules.Identity.Contracts.Grants;

namespace IntoChat.OS.Modules.Identity.Hosting;

/// <summary>
/// Fail-closed startup verification for access control and grain call filtering.
/// Ensures the silo cannot start with missing or inappropriately disabled access policies or call filters.
/// </summary>
public sealed class AccessPolicyValidation(IServiceProvider services) : IStartupTask, IHostedService
{
    public Task Execute(CancellationToken cancellationToken) => ValidateAsync();

    public Task StartAsync(CancellationToken cancellationToken) => ValidateAsync();

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private Task ValidateAsync()
    {
        // 1. Eagerly initialize presence proof (so local Open hosts mint run/presence on boot) and IBrainAccess check
        _ = services.GetService<IPresenceProof>();
        _ = services.GetService<DigitalBrain.Runtime.Http.Surface.IBrainAccess>();

        // 2. Required call filter stage verification (fail closed)
        var stages = services.GetServices<ICallFilterStage>().ToArray();
        if (stages.Length == 0)
        {
            throw new InvalidOperationException("Register an authorization policy or AddIntoChatOSPlatform before starting the server.");
        }

        // 3. Grain call filter verification (fail closed)
        var grainFilters = services.GetServices<IIncomingGrainCallFilter>().ToArray();
        if (!grainFilters.OfType<SiloAccessCallFilter>().Any())
        {
            throw new InvalidOperationException("Fail-closed security check: SiloAccessCallFilter is not registered as an IIncomingGrainCallFilter on the silo.");
        }

        // 4. Grant policy source check if GrantCallFilterStage is registered
        if (stages.OfType<GrantCallFilterStage>().Any())
        {
            var source = services.GetService<IGrantPolicySource>();
            if (source is null)
            {
                throw new InvalidOperationException("Fail-closed security check: GrantCallFilterStage is registered but IGrantPolicySource is missing.");
            }
        }

        return Task.CompletedTask;
    }
}
