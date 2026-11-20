using IntoChat.OS.Host.Cluster;

namespace IntoChat.OS.Modules.Microsoft.Orleans;

public static class OrleansModuleOptionsExtensions
{
    public static OrleansOptions WithAzurite(this OrleansOptions options, Action<AzuriteOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Azurite.Enabled = true;
        configure?.Invoke(options.Azurite);
        return options;
    }
}
