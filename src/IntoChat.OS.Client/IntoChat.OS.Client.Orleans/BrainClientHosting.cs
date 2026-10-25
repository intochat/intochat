using IntoChat.OS.Contracts.Configuration;
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
        services.AddOptions<SubscriptionOptions>()
            .Validate(o => o.BufferCapacity > 0 && o.RenewEvery > TimeSpan.Zero && o.OperationTimeout > TimeSpan.Zero, "Invalid brain buffer or subscription timing settings.")
            .ValidateOnStart();
        services.TryAddSingleton<BrainClient>();
        services.TryAddSingleton<IIntoChatOSBrains>(provider => provider.GetRequiredService<BrainClient>());
        services.TryAddSingleton<IBrainSubscriptions>(provider => provider.GetRequiredService<BrainClient>());
        return services;
    }
}
