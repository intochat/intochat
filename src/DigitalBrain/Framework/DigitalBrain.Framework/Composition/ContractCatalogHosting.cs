using DigitalBrain.Framework.Vectors;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DigitalBrain.Framework;

public static class ContractCatalogHosting
{
    public static IServiceCollection AddContractCatalog(this IServiceCollection services, bool lexicalFallback = false)
    {
        services.TryAddSingleton(provider => new ContractCatalog(provider.GetRequiredService<ModuleInventory>()));
        services.TryAddSingleton(provider => new NeuronTypeSearch(provider.GetRequiredService<ContractCatalog>(),
            provider.GetService<IVectorStore>(), Embeddings(provider), lexicalFallback));
        return services;
    }

    private static IEmbeddingGenerator<string, Embedding<float>>? Embeddings(IServiceProvider services)
    {
        try { return services.GetService<IEmbeddingGenerator<string, Embedding<float>>>(); }
        catch (InvalidOperationException) { return null; }
    }
}
