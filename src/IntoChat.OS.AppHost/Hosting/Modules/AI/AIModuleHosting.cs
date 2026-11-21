using DigitalBrain.Modules.Options;
using IntoChat.OS.Aspire.Hosting;
using IntoChat.OS.Modules.AI.Models;

namespace IntoChat.OS.Modules.AI.Aspire.Hosting;

public sealed class AIModuleHosting : ModuleHosting<AIModule>
{
    public override void Configure(IntoChatOSBuilder brain)
        => AIHostingExtensions.ConfigureDeclaration(brain,
            brain.GetModuleConfiguration("ai").GetModuleOptions<AIOptions>("ai"));
}

public static partial class AIHostingExtensions
{
    internal static void ConfigureDeclaration(IntoChatOSBuilder brain, AIOptions options)
    {
        var module = new IntoChatOSModuleBuilder<AIModuleHosting>(brain);
        var state = State(module);
        state.EnableSensitiveData = options.Telemetry.EnableSensitiveData ?? false;
        state.SetRoles(options.Roles);

        foreach (var name in options.Hosting.Llms)
        {
            var card = KnownModels.Find(name) ?? throw new InvalidOperationException(
                $"'{name}' is not a catalogued LLM. Known: {string.Join(", ", KnownModels.All.Where(static c => c.Can.HasFlag(Abilities.Text)).Select(static c => c.Id.Value))}.");
            state.AddModel(card);
        }

        foreach (var name in options.Hosting.Embeddings)
        {
            var card = KnownModels.Find(name) ?? throw new InvalidOperationException(
                $"'{name}' is not a catalogued embedding model. Known: {string.Join(", ", KnownModels.All.Where(static c => c.Can.HasFlag(Abilities.Embed)).Select(static c => c.Id.Value))}.");
            state.AddModel(card);
        }

        var roleMap = options.Roles.ToRoleMap();
        if (TryRole(roleMap, Role.Hear, out var hearId))
        {
            var card = KnownModels.Find(hearId.Value) ?? throw new InvalidOperationException(
                $"'{hearId.Value}' is not a catalogued transcription model. Known: {string.Join(", ", KnownModels.All.Where(static c => c.Can.HasFlag(Abilities.Hear)).Select(static c => c.Id.Value))}.");
            var voice = module.GetOrAddProjection(static b => new VoiceToTextHostingState(b));
            voice.SetModel(card);
        }

        if (TryRole(roleMap, Role.Fast, out var fastId) && KnownModels.Find(fastId.Value) is { } fastCard && IsSystemOne(fastCard))
        {
            var systemOne = module.GetOrAddProjection(b => new SystemOneHostingState(b, module.Resource));
            if (fastCard.Provider is AiProvider.Ollama) { systemOne.SetDefault(fastCard, state.AddOllamaBackingModel(fastCard.Id.Value)); }
            else { systemOne.SetDefault(fastCard, null); }
        }
        else
        {
            foreach (var name in options.Hosting.SystemOnes)
            {
                var card = KnownModels.Find(name) ?? throw new InvalidOperationException(
                    $"'{name}' is not a catalogued System One model.");
                if (!IsSystemOne(card)) { continue; }
                var systemOne = module.GetOrAddProjection(b => new SystemOneHostingState(b, module.Resource));
                if (card.Provider is AiProvider.Ollama) { systemOne.SetDefault(card, state.AddOllamaBackingModel(card.Id.Value)); }
                else { systemOne.SetDefault(card, null); }
            }
        }
    }

    private static bool TryRole(RoleMap map, Role role, out ModelId id)
        => map.Device.TryGetValue(role, out id) || map.Cloud.TryGetValue(role, out id);

    private static bool IsSystemOne(ModelCard card)
        => card.Provider == AiProvider.TypeSafe || card.Id == KnownModels.LocalJev.Id;
}
