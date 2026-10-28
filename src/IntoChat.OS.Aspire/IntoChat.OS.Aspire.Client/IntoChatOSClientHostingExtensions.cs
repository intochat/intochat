using DigitalBrain.Runtime.Orleans;
using IntoChatOSNames = IntoChat.OS.Contracts.Configuration.IntoChatOSNames;
using IntoChat.OS;
using IntoChat.OS.Client;
using IntoChat.OS.Client.Orleans;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.ServiceDefaults;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using DigitalBrain.Spi;

namespace IntoChat.OS.Aspire.Client;

public static class IntoChatOSClientHostingExtensions
{
    public static TBuilder AddIntoChatOSClient<TBuilder>(this TBuilder builder, bool useAzureClustering = false)
        where TBuilder : IHostApplicationBuilder
    {
        // Every IntoChat.OS client carries the process defaults; hosts cannot forget them.
        builder.AddServiceDefaults();
        if (useAzureClustering)
        {
            builder.AddKeyedAzureTableServiceClient(IntoChatOSNames.Clustering);
        }
        builder.UseOrleansClient(client => client.AddIntoChatOS());
        // The kernel view reads the instance directory through the cluster (the kernel composition, 06-02).
        builder.Services.TryAddSingleton<IBrainObserver>(IntoChat.OS.Kernel.Composition.Kernel.For);
        return builder;
    }
}
