using DigitalBrain;
using DigitalBrain.Capabilities;
using DigitalBrain.Modules;
using DigitalBrain.Runtime.Http;
using IntoChat.Cloud.Mail;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace IntoChat.Cloud.Organisations;

// Personal organisations, membership and invitations. Composed by the cloud host.
[Module(Id)]
public sealed class OrganisationsModule : Module
{
    public const string Id = "organisations";
    public const string OptionsSection = "IntoChatOS:Modules:" + Id + ":Options";

    public static System.Reflection.Assembly? Contracts { get; } = typeof(IOrganisation).Assembly;

    public static ModuleManifest Manifest { get; } = new(
        Provides: [],
        Requires: [Capability.Identity.V1],
        Needs: [Need.Http],
        Settings: [Setting.Of<OrganisationsModuleOptions>(OptionsSection)],
        Secrets: []);

    public void ConfigureServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.AddOptions<OrganisationsModuleOptions>().BindConfiguration(OptionsSection);
        services.TryAddSingleton(provider => provider.GetRequiredService<IOptions<OrganisationsModuleOptions>>().Value);
        services.TryAddSingleton<IPersonalOrganisationGuard, AllowAllPersonalOrganisationGuard>();
        services.TryAddSingleton<IOrganisationMail, UnconfiguredOrganisationMail>();
        services.AddModuleHttpSurface<InviteHttpSurface>();
        services.AddSingleton<IHostedService, OrganisationsStartup>();
    }
}

internal sealed class OrganisationsStartup(OrganisationsModuleOptions options) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        options.Validate();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
