using DigitalBrain.AI;
using DigitalBrain.AI.Models;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Modules.AI.Tests;

internal static class ModelCatalogServices
{
    // The catalog a hand-built runner host needs: default options and no ready provider, so a turn resolves to the host client.
    public static IServiceCollection AddModelCatalog(this IServiceCollection services)
        => services.AddOptions().AddSingleton<IAiCredentials>(new FixedAiCredentials()).AddSingleton<ModelCatalog>();
}
