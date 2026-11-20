using System.Text;
using DigitalBrain.Capabilities.Connections.Sealing;
using DigitalBrain.Identity.Local.Vault;
using IntoChat.OS;
using IntoChat.OS.Connections;
using IntoChat.OS.Framework.Modules;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using IntoChat.OS.Host.Configuration;

namespace IntoChat.OS.Authority.Secrets;

public static class MasterKeyServiceCollectionExtensions
{
    public static IServiceCollection AddMasterKeyWrapper(this IServiceCollection services)
    {
        services.AddOptions<MasterKeyOptions>().BindConfiguration(MasterKeyOptions.SectionName)
            .Validate(options => !string.IsNullOrWhiteSpace(options.MasterKey),
                $"Configure {IntoChatOSNames.MasterKeyConfigurationKey} via the {IntoChatOSNames.MasterKeyEnvironmentVariable} environment secret before starting the host.")
            .ValidateOnStart();
        services.TryAddSingleton<IKeyWrapper, ConfiguredKeyWrapper>();
        services.TryAddSingleton<ISecretSealer, SecretSealer>();
        return services;
    }

    private sealed class ConfiguredKeyWrapper(IOptions<MasterKeyOptions> options) : IKeyWrapper
    {
        private readonly MasterKeyWrapper _inner = Create(options);

        public string Wrap(byte[] key) => _inner.Wrap(key);

        public byte[] Unwrap(string wrapped) => _inner.Unwrap(wrapped);

        private static MasterKeyWrapper Create(IOptions<MasterKeyOptions> options)
        {
            ArgumentNullException.ThrowIfNull(options);
            var master = options.Value?.MasterKey;
            if (string.IsNullOrWhiteSpace(master))
            {
                throw new InvalidOperationException($"{IntoChatOSNames.MasterKeyConfigurationKey} is not configured.");
            }

            return new MasterKeyWrapper(Encoding.UTF8.GetBytes(master));
        }
    }
}
