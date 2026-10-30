using IntoChat.OS;
using IntoChat.OS.Connections;
using IntoChat.OS.Connections.Platform.Connections;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework.Modules;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IntoChat.OS.Authority.Secrets;

public static class MasterKeyServiceCollectionExtensions
{
    public static IServiceCollection AddMasterKeyWrapper(this IServiceCollection services)
    {
        services.AddOptions<MasterKeyOptions>().BindConfiguration(MasterKeyOptions.SectionName)
            .Validate(options => !string.IsNullOrWhiteSpace(options.MasterKey),
                $"Configure {IntoChatOSNames.MasterKeyConfigurationKey} via the {IntoChatOSNames.MasterKeyEnvironmentVariable} environment secret before starting the host.")
            .ValidateOnStart();
        services.TryAddSingleton<IKeyWrapper, MasterKeyWrapper>();
        services.TryAddSingleton<ISecretSealer, SecretSealer>();
        return services;
    }
}
