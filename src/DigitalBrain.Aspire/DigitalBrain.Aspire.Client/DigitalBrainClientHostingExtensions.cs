using DigitalBrain;
using DigitalBrain.Client;
using DigitalBrain.Client.Orleans;
using DigitalBrain.Framework;
using DigitalBrain.ServiceDefaults;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;

namespace DigitalBrain.Aspire.Client;

public static class DigitalBrainClientHostingExtensions
{
    public static TBuilder AddDigitalBrainClient<TBuilder>(this TBuilder builder, bool useAzureClustering = false)
        where TBuilder : IHostApplicationBuilder
    {
        // Every DigitalBrain client carries the process defaults; hosts cannot forget them.
        builder.AddServiceDefaults();
        if (useAzureClustering)
        {
            builder.AddKeyedAzureTableServiceClient(DigitalBrainNames.Clustering);
        }
        builder.UseOrleansClient(client => client.AddDigitalBrain());
        return builder;
    }
}
