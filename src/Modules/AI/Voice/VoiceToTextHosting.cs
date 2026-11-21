using IntoChat.OS.Host.Configuration;
using IntoChat.OS.Modules.AI.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Modules.AI;

internal static class VoiceToTextHosting
{
    public static readonly string RolesKey = AIKeys.Roles;

    internal static void Add(IServiceCollection services, IConfiguration configuration)
        => Add(services, AIOptionsRuntime.Read(configuration), configuration[IntoChatOSNames.TopologyKey]);

    internal static void Add(IServiceCollection services, AIOptions configuration, string? topology = null)
    {
        services.TryAddSingleton<IAudioConverter, OggOpusToWavConverter>();

        var configuredId = RoleMaps.Explicit(configuration, topology, Role.Hear);
        if (configuredId is not { Value.Length: > 0 } id)
        {
            services.TryAddSingleton<IAudioTranscriptionService, UnavailableTranscriptionService>();
            return;
        }

        var card = KnownModels.Find(id.Value);
        if (card is null || !card.Can.HasFlag(Abilities.Hear))
        {
            // An unknown model must not take the kernel down over voice: the
            // endpoint reports it as 503 with this message, and every other
            // surface keeps working.
            services.TryAddSingleton<IAudioTranscriptionService>(
                new UnavailableTranscriptionService(
                    $"{RolesKey} names unknown transcription model '{id.Value}'. "
                    + $"Known models: {string.Join(", ", KnownModels.All.Where(static c => c.Can.HasFlag(Abilities.Hear)).Select(static c => c.Id.Value))}."));
            return;
        }

        // The provider decides the implementation — one key, no second flag.
        switch (card.Provider)
        {
            case AiProvider.FoundryLocal:
                services.TryAddSingleton(sp => new FoundryLocalTranscriptionService(
                    sp.GetRequiredService<IOptions<AIOptions>>(),
                    sp.GetRequiredService<global::Microsoft.Extensions.Logging.ILogger<FoundryLocalTranscriptionService>>(),
                    sp.GetRequiredService<IAudioConverter>()));
                services.TryAddSingleton<IAudioTranscriptionService>(static sp =>
                    sp.GetRequiredService<FoundryLocalTranscriptionService>());
                services.AddHostedService(static sp =>
                    sp.GetRequiredService<FoundryLocalTranscriptionService>());
                break;

            case AiProvider.OpenAI:
                services.TryAddSingleton<IAudioTranscriptionService>(sp =>
                    new OpenAITranscriptionService(card, sp.GetRequiredService<IAiCredentials>()));
                break;

            default:
                services.TryAddSingleton<IAudioTranscriptionService>(
                    new UnavailableTranscriptionService(
                        $"{card.Name} is served by {card.Provider}, which has no "
                        + "transcription implementation."));
                break;
        }
    }
}
