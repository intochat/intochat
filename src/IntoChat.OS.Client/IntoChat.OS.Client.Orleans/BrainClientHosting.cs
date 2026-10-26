using IntoChat.OS.Framework;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IntoChat.OS.Client.Orleans;

public static class BrainClientHosting
{
    public static IClientBuilder AddIntoChatOS(this IClientBuilder client)
    {
        client.Services.AddIntoChatOSClient();
        return client;
    }

    public static IServiceCollection AddIntoChatOSClient(this IServiceCollection services)
    {
        services.TryAddSingleton<BrainClient>();
        services.TryAddSingleton<IIntoChatOSBrains>(provider => provider.GetRequiredService<BrainClient>());
        return services;
    }
}
