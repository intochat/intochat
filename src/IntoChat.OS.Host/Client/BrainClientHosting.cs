using DigitalBrain.Runtime.Orleans.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using IntoChat.OS.Host.Brains;

namespace IntoChat.OS.Host.Client;

public static class BrainClientHosting
{
    public static IServiceCollection AddIntoChatOSClient(this IServiceCollection services)
    {
        services.TryAddSingleton<BrainClient>();
        services.TryAddSingleton<IIntoChatOSBrains>(provider => provider.GetRequiredService<BrainClient>());
        services.AddNeuronProxies();
        return services;
    }
}
