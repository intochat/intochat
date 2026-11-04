using DigitalBrain;
using DigitalBrain.Capabilities;
using DigitalBrain.Spi;
using IntoChat.Cloud.Mail.Templates;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace IntoChat.Cloud.Mail;

// Transactional mail. ACS is the real sender; Log is the development and test sender. Loaded only when Topology=cloud.
[Module(Id)]
public sealed class MailModule : Module
{
    public const string Id = "mail";
    public const string OptionsSection = "IntoChatOS:Modules:" + Id + ":Options";

    public static System.Reflection.Assembly? Contracts { get; } = typeof(IMailRate).Assembly;

    public static bool LoadsUnder(string? topology) => string.Equals(topology?.Trim(), "cloud", StringComparison.OrdinalIgnoreCase);

    public static ModuleManifest Manifest { get; } = new(
        Provides: [],
        Requires: [Capability.Secrets.V1],
        Needs: [Need.Secrets],
        Settings: [Setting.Of<MailModuleOptions>(OptionsSection)],
        Secrets: [Secret.Named(MailSecrets.AcsConnection), Secret.Named(MailSecrets.RecipientHashing)]);

    public void ConfigureServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.AddOptions<MailModuleOptions>().BindConfiguration(OptionsSection);
        services.TryAddSingleton(provider => provider.GetRequiredService<IOptions<MailModuleOptions>>().Value);
        services.TryAddSingleton<MailTemplateRenderer>();
        services.TryAddSingleton<MailSecrets>();
        services.TryAddSingleton<HmacRecipientHasher>();
        services.TryAddSingleton<IRecipientHasher>(provider => provider.GetRequiredService<HmacRecipientHasher>());
        services.TryAddSingleton<IAcsEmailTransport, AcsEmailTransport>();
        services.TryAddSingleton<AcsMailSender>();
        services.TryAddSingleton<LogMailSender>();
        services.AddSingleton<IHostedService, MailStartup>();
        services.TryAddSingleton<IMailSender>(provider =>
        {
            var options = provider.GetRequiredService<MailModuleOptions>();
            IMailSender inner = options.Provider == MailProvider.Log
                ? provider.GetRequiredService<LogMailSender>()
                : provider.GetRequiredService<AcsMailSender>();
            return new RateLimitedMailSender(inner, provider.GetRequiredService<IRecipientHasher>(), provider.GetRequiredService<INeuronProxies>(), provider.GetService<IMailHost>());
        });
    }
}

internal sealed class MailStartup(MailModuleOptions options, MailSecrets secrets, HmacRecipientHasher hasher, IHostEnvironment environment) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (options.Provider == MailProvider.Log && !environment.IsDevelopment())
        {
            throw new InvalidOperationException("Provider=Log is allowed only in Development.");
        }

        var key = await secrets.Named(MailSecrets.RecipientHashing);
        if (!string.IsNullOrWhiteSpace(key)) { hasher.UseKey(key); }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
