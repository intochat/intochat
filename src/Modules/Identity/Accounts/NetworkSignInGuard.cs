using DigitalBrain.Capabilities.Identity;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using IntoChat.OS.Host.ServiceDefaults;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;

namespace IntoChat.OS.Modules.Identity.Accounts;

// Applies NetworkSignIn to this host, in every topology. Before anything binds it reads the listeners configuration names and
// refuses the start; the identity routes and the anonymous list read the same verdict (Configured). After the bind it reads the
// addresses the server really bound, because a listener set in code is invisible to configuration, and stops a host that the
// rule refuses or that mapped the built-in registration believing it was on loopback.
internal sealed class NetworkSignInGuard(IServiceProvider services, IHostApplicationLifetime lifetime, ILogger<NetworkSignInGuard> logger,
    OpenPostureLoopback.OpenPostureExit exit) : IHostedLifecycleService
{
    // A sign-in module took sign-in over: it switched the built-in password routes off and bound the sessions over its own
    // port. The option alone can come from configuration, so it does not count.
    public static bool SignInModulePresent(IServiceProvider services)
        => !Identity(services).BuiltInPasswordEndpoints
            && services.GetService<IServiceProviderIsService>()?.IsService(typeof(IIdentitySessionPort)) == true;

    public static NetworkSignIn.Verdict Configured(IServiceProvider services)
    {
        var configuration = Configuration(services);
        return Decide(services, NetworkSignIn.Exposed(configuration), configuration);
    }

    public Task StartingAsync(CancellationToken cancellationToken)
    {
        if (Configured(services) is { Starts: false, Refusal: var refusal }) { throw new InvalidOperationException(refusal); }
        return Task.CompletedTask;
    }

    public Task StartedAsync(CancellationToken cancellationToken)
    {
        var addresses = Bound();
        if (addresses is null) { return Task.CompletedTask; }
        // Some servers publish addresses only once the application has started.
        if (addresses.Length == 0) { lifetime.ApplicationStarted.Register(() => Inspect(Bound() ?? [])); }
        else { Inspect(addresses); }
        return Task.CompletedTask;
    }

    private void Inspect(string[] addresses)
    {
        var bound = addresses.Where(address => !OpenPostureLoopback.IsLoopbackUrl(address)).ToArray();
        if (bound.Length == 0) { return; }
        var verdict = Decide(services, bound, Configuration(services));
        if (!verdict.Starts)
        {
            logger.LogError("{Refusal}", verdict.Refusal);
        }
        else if (Configured(services).OpenRegistration)
        {
            logger.LogError("The built-in registration is open, and a listener bound in code is beyond loopback: {Urls}. Name the listener in configuration (urls or {BindKey}) so registration is closed there, or bind loopback.",
                string.Join(", ", bound), BindPolicy.BindKey);
        }
        else { return; }
        exit.Fail();
        lifetime.StopApplication();
    }

    private string[]? Bound() => services.GetService<IServer>()?.Features.Get<IServerAddressesFeature>()?.Addresses.ToArray();

    private static NetworkSignIn.Verdict Decide(IServiceProvider services, IReadOnlyList<string> exposed, IConfiguration configuration)
        => NetworkSignIn.Decide(exposed, SignInModulePresent(services), Identity(services).BuiltInPasswordEndpoints, configuration);

    private static IdentityHostOptions Identity(IServiceProvider services)
        => services.GetService<IOptions<IdentityHostOptions>>()?.Value ?? DefaultIdentity;

    private static IConfiguration Configuration(IServiceProvider services) => services.GetService<IConfiguration>() ?? NoConfiguration;

    private static readonly IdentityHostOptions DefaultIdentity = new();
    private static readonly IConfiguration NoConfiguration = new ConfigurationBuilder().Build();

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
