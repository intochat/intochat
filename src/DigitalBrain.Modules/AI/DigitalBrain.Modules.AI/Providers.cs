using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.AI;

internal static class Providers
{
    // An empty provider/model pair means the host's default chat client, which the
    // caller does not own. An explicit selection is resolved through the configured
    // profiles and presets, and its pipeline is owned by the caller.
    internal static async Task<(IChatClient Client, bool OwnsClient)> Resolve(IServiceProvider services, string? provider, string? model)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (string.IsNullOrWhiteSpace(provider) && string.IsNullOrWhiteSpace(model))
        {
            return (services.GetRequiredService<IChatClient>(), false);
        }

        var profiles = services.GetRequiredService<ModelProfiles>();
        return (await profiles.CreateClient(await profiles.Resolve(new AgentModelSelection(Provider: provider, Model: model))), true);
    }
}
