using DigitalBrain.AI;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Modules.Assistant.Tests;

internal static class ModelCatalogServices
{
    // The catalog a hand-built runner host needs: default options and no ready provider, so a turn resolves to the host client.
    public static IServiceCollection AddModelCatalog(this IServiceCollection services)
        => services.AddOptions().AddSingleton<IAiCredentials>(new FixedAiCredentials()).AddSingleton<ModelCatalog>();
}
