using DigitalBrain.Kernel;
using DigitalBrain.Runtime.Orleans;
using IntoChat.OS;
using IntoChat.OS.Client.Orleans;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Kernel.Composition.Kernel;
using IntoChat.OS.ServiceDefaults;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using IntoChatOSNames = IntoChat.OS.Contracts.Configuration.IntoChatOSNames;

namespace IntoChat.OS.Kernel.AspireClient;

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
        builder.UseOrleansClient(client =>
        {
            client.AddIntoChatOS();
            IntoChat.OS.Kernel.AspireServer.OrleansAotResponses.Register(client.Services);
        });
        // The kernel view reads the instance directory through the cluster (the kernel composition, 06-02).
        builder.Services.TryAddSingleton<IBrainObserver>(IntoChat.OS.Kernel.Composition.Kernel.OsKernel.For);
        return builder;
    }
}
