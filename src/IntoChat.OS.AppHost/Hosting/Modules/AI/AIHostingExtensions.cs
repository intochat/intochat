using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using DigitalBrain.Modules.Configuration;
using IntoChat.OS;
using IntoChat.OS.Aspire.Hosting;
using IntoChat.OS.Modules.AI.Models.FoundryLocal;
using IntoChat.OS.Host.Configuration;

namespace IntoChat.OS.Modules.AI.Aspire.Hosting;

public static partial class AIHostingExtensions
{
    private static readonly string EnableSensitiveDataEnvironmentKey =
        ModuleConfiguration.EnvironmentName(AIKeys.TelemetryEnableSensitiveData);

    // The declaration API is the typed AIOptions extension family (WithLlm<TModel> and
    // friends): typed at the declaration site, carried by configuration — the one value
    // channel — and resolved loudly once at bind in ConfigureDeclaration.
    private static AIHostingState State(IntoChatOSModuleBuilder<IntoChat.OS.Modules.AI.Aspire.Hosting.AIModuleHosting> module)
    {
        ArgumentNullException.ThrowIfNull(module);
        var state = module.GetOrAddProjection(brain => new AIHostingState(brain, module.Resource));

        return state;
    }

}
