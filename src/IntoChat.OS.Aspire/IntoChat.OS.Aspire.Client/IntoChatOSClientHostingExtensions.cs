using IntoChat.OS;
using IntoChat.OS.Client;
using IntoChat.OS.Client.Orleans;
using IntoChat.OS.Framework;
using IntoChat.OS.ServiceDefaults;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;

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
        return builder;
    }
}
