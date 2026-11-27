using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using DigitalBrain.Capabilities.Secrets;
using DigitalBrain.Identity.Local.Custody;
using DigitalBrain.Identity.Local.Vault;
using IntoChat.OS.Modules.Infrastructure.Secrets.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IntoChat.OS.Modules.Infrastructure.Secrets;

/// <summary>
/// Registers the configured pluggable <see cref="ISecretStoreBackend"/> with fail-closed startup validation.
/// Supported values for <c>IntoChatOS:Secrets:Backend</c>:
/// <list type="bullet">
/// <item><c>Default</c> / empty: built-in Orleans grain state (<c>mydata.state</c>).</item>
/// <item><c>LocalFile</c>: encrypted local file backend (<see cref="LocalFileSecretStoreBackend"/>).</item>
/// <item><c>AzureKeyVault</c>: <see cref="AzureKeyVaultSecretStoreBackend"/>.</item>
/// <item><c>Bitwarden</c>: <see cref="BitwardenSecretStoreBackend"/>.</item>
/// </list>
/// Any unknown backend name fails closed at startup.
/// </summary>
internal static class SecretsBackendHosting
{
    public const string BackendConfigKey = "IntoChatOS:Secrets:Backend";
    public const string LocalFileDirectoryConfigKey = "IntoChatOS:Secrets:LocalFile:Directory";

    public static IServiceCollection AddSecretsBackend(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var backend = configuration[BackendConfigKey]?.Trim();
        if (string.IsNullOrEmpty(backend) || string.Equals(backend, "Default", StringComparison.OrdinalIgnoreCase))
        {
            return services;
        }

        if (string.Equals(backend, "LocalFile", StringComparison.OrdinalIgnoreCase))
        {
            var directory = configuration[LocalFileDirectoryConfigKey];
            if (string.IsNullOrWhiteSpace(directory))
            {
                throw new InvalidOperationException($"{LocalFileDirectoryConfigKey} must be configured when {BackendConfigKey} is 'LocalFile'.");
            }

            services.TryAddSingleton<ISecretStoreBackend>(sp =>
                new LocalFileSecretStoreBackend(directory, sp.GetRequiredService<IMasterKeyCustody>()));
            return services;
        }

        if (string.Equals(backend, "AzureKeyVault", StringComparison.OrdinalIgnoreCase))
        {
            var options = new AzureKeyVaultSecretStoreOptions();
            configuration.GetSection(AzureKeyVaultSecretStoreOptions.SectionName).Bind(options);
            options.Validate();

            services.TryAddSingleton(options);
            services.TryAddSingleton<IAzureKeyVaultSecretClient>(_ =>
                new AzureSdkKeyVaultSecretClient(new SecretClient(options.VaultUri!, new DefaultAzureCredential())));
            services.TryAddSingleton<ISecretStoreBackend>(sp => new AzureKeyVaultSecretStoreBackend(
                sp.GetRequiredService<IAzureKeyVaultSecretClient>(),
                sp.GetRequiredService<IMasterKeyCustody>(),
                sp.GetRequiredService<AzureKeyVaultSecretStoreOptions>()));
            return services;
        }

        if (string.Equals(backend, "Bitwarden", StringComparison.OrdinalIgnoreCase))
        {
            var options = new BitwardenSecretStoreOptions();
            configuration.GetSection(BitwardenSecretStoreOptions.SectionName).Bind(options);
            options.Validate();

            services.TryAddSingleton(options);
            services.AddHttpClient(HttpBitwardenSecretsClient.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(30));
            services.TryAddSingleton<IBitwardenSecretsClient>(sp => new HttpBitwardenSecretsClient(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpBitwardenSecretsClient.HttpClientName),
                sp.GetRequiredService<BitwardenSecretStoreOptions>()));
            services.TryAddSingleton<ISecretStoreBackend>(sp => new BitwardenSecretStoreBackend(
                sp.GetRequiredService<IBitwardenSecretsClient>(),
                sp.GetRequiredService<IMasterKeyCustody>(),
                sp.GetRequiredService<BitwardenSecretStoreOptions>()));
            return services;
        }

        throw new InvalidOperationException(
            $"Unknown secrets backend '{backend}' in {BackendConfigKey}. Supported values: Default, LocalFile, AzureKeyVault, Bitwarden.");
    }
}
