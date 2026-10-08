using DigitalBrain.Framework.Modules;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DigitalBrain.Framework.Contracts;

public static class ContractCatalogHosting
{
    public static IServiceCollection AddContractCatalog(this IServiceCollection services)
    {
        services.TryAddSingleton(provider => new ContractCatalog(provider.GetRequiredService<ModuleInventory>()));
        services.TryAddSingleton(provider => new NeuronTypeSearch(provider.GetRequiredService<ContractCatalog>(), Embeddings(provider)));
        return services;
    }

    private static IEmbeddingGenerator<string, Embedding<float>>? Embeddings(IServiceProvider services)
    {
        try { return services.GetService<IEmbeddingGenerator<string, Embedding<float>>>(); }
        catch (InvalidOperationException) { return null; }
    }
}
