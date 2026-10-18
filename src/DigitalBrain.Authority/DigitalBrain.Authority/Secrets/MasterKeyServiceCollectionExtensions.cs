using DigitalBrain;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Connections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DigitalBrain.Authority.Secrets;

public static class MasterKeyServiceCollectionExtensions
{
    public static IServiceCollection AddMasterKeyWrapper(this IServiceCollection services)
    {
        services.AddOptions<MasterKeyOptions>().BindConfiguration(MasterKeyOptions.SectionName)
            .Validate(options => !string.IsNullOrWhiteSpace(options.MasterKey),
                $"Configure {DigitalBrainNames.MasterKeyConfigurationKey} via the {DigitalBrainNames.MasterKeyEnvironmentVariable} environment secret before starting the host.")
            .ValidateOnStart();
        services.TryAddSingleton<IKeyWrapper, MasterKeyWrapper>();
        services.TryAddSingleton<ISecretSealer, SecretSealer>();
        return services;
    }
}
