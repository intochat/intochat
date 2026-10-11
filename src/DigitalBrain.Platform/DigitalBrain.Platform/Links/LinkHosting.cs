using System.Runtime.Versioning;
using DigitalBrain.Framework.Links;
using DigitalBrain.Framework.Platform.Identity;
using DigitalBrain.Framework.Platform.Marketplace;
using DigitalBrain.Platform.Trust;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DigitalBrain.Platform.Links;

public static class LinkHosting
{
    // A machine of a separate trust cluster (DigitalBrain:Platform:Trust:Endpoint set): its links to the trust host
    // and to other machines, and the five trust clients over the trust link (T20-7). Registered ahead of
    // AddTrustClients, whose grain clients are then never used on this host.
    public static ISiloBuilder AddMachineLinks(this ISiloBuilder silo)
    {
        ArgumentNullException.ThrowIfNull(silo);
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            Links(silo.Services, bindClients: true);
            return silo;
        }
        throw new PlatformNotSupportedException("Machine links need QUIC (Windows, Linux or macOS).");
    }

    [SupportedOSPlatform("windows"), SupportedOSPlatform("linux"), SupportedOSPlatform("macos")]
    private static void Links(IServiceCollection services, bool bindClients)
    {
        if (services.Any(service => service.ServiceType == typeof(QuicMachineTransport))) { return; }
        services.AddOptions<LinkOptions>().BindConfiguration(LinkOptions.Key)
            .Validate(links => { links.Validate(); return true; }).ValidateOnStart();
        services.AddOptions<TrustOptions>().Validate<IHostEnvironment>((trust, environment) =>
        {
            TrustPin.RequireInProduction(environment.EnvironmentName, trust);
            return true;
        }).ValidateOnStart();
        services.TryAddSingleton<TrustPin>();
        services.AddSingleton(provider =>
        {
            var transport = ActivatorUtilities.CreateInstance<QuicMachineTransport>(provider);
            transport.RendezvousHandler = new RendezvousHandler(transport, provider.GetRequiredService<ILogger<RendezvousHandler>>());
            return transport;
        });
        services.AddSingleton<IMachineTransport>(provider => provider.GetRequiredService<QuicMachineTransport>());
        services.AddSingleton(provider => new TrustSigner(provider.GetRequiredService<Sequencer.MachineIdentity>(),
            provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<MachineCounter>()));
        services.AddSingleton(provider => new LinkTrustClients(provider.GetRequiredService<QuicMachineTransport>(),
            provider.GetRequiredService<Orleans.Serialization.Serializer>(), provider.GetRequiredService<TrustSigner>())
        { PairingSecretHash = MachinePairing.HashOf(provider.GetRequiredService<IOptions<Parts.PartsOptions>>().Value) });
        if (!bindClients) { return; }
        services.AddSingleton<ISequencerClient>(provider => provider.GetRequiredService<LinkTrustClients>());
        services.AddSingleton<INodeDirectoryClient>(provider => provider.GetRequiredService<LinkTrustClients>());
        services.AddSingleton<IKeyringClient>(provider => provider.GetRequiredService<LinkTrustClients>());
        services.AddSingleton<IMarketplaceClient>(provider => provider.GetRequiredService<LinkTrustClients>());
        services.AddSingleton<IIdentityClient>(provider => provider.GetRequiredService<LinkTrustClients>());
    }

    // R93: an Orleans-client host of the deployment (the MCP) is a machine that holds no part: its own identity in its
    // own MachineDirectory, registered with the trust host for the owner's account (R41/R52), and its own trust link.
    // It only dials (the trust link is outbound), so it needs no link port of its own. The caller binds the clients.
    internal static void AddClientMachine(this IServiceCollection services)
    {
        if (!(OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())) { return; }
        services.AddOptions<Parts.PartsOptions>().BindConfiguration(Parts.PartsOptions.Key)
            .Validate(parts => { parts.Validate(); return true; });
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<DigitalBrain.Framework.Parts.IMachineSigningKey>(provider => Sequencer.FileMachineSigningKey.LoadOrCreate(Path.Combine(
            provider.GetRequiredService<IOptions<Parts.PartsOptions>>().Value.MachineDirectory, "identity.key")));
        services.TryAddSingleton<DigitalBrain.Framework.Parts.IMachineAgreementKey>(provider => DigitalBrain.Framework.Parts.SoftwareMachineAgreementKey.LoadOrCreate(Path.Combine(
            provider.GetRequiredService<IOptions<Parts.PartsOptions>>().Value.MachineDirectory, "agreement.key")));
        services.TryAddSingleton(provider => new Sequencer.MachineIdentity(provider.GetRequiredService<DigitalBrain.Framework.Parts.IMachineSigningKey>(),
            provider.GetRequiredService<DigitalBrain.Framework.Parts.IMachineAgreementKey>()));
        services.TryAddSingleton<TrustDeployment>();
        services.TryAddSingleton(provider => new MachineCounter(() => Path.Combine(provider.GetRequiredService<TrustDeployment>().Directory, "counter")));
        services.TryAddSingleton<MachineRegistrar>();
        Links(services, bindClients: false);
        services.AddHostedService<ClientMachineRegistration>();
    }
}

// R93: at start a client machine learns the deployment and binds itself to the account it serves, exactly as a
// machine silo's MachineRegistration does, but provisions nothing: the owner's machines do that.
internal sealed class ClientMachineRegistration(IServiceProvider services, IOptions<TrustOptions> trust,
    IOptions<DigitalBrain.Platform.Identity.Configuration.AuthOptions> auth) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(trust.Value.Endpoint)) { return; }
        var account = auth.Value.Posture == DigitalBrain.Platform.Identity.Configuration.IdentityPosture.Open
            ? DigitalBrain.Platform.Identity.AccountSession.DefaultLogin
            : auth.Value.Username;
        await services.GetRequiredService<TrustDeployment>().Resolve();
        if (account is { Length: > 0 }) { await services.GetRequiredService<MachineRegistrar>().Bind(account); }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
