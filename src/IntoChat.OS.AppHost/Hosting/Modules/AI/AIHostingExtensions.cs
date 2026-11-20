using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using DigitalBrain.Modules.Configuration;
using IntoChat.OS.Aspire.Hosting;

namespace IntoChat.OS.Modules.AI.Aspire.Hosting;

public static partial class AIHostingExtensions
{
    private static readonly string EnableSensitiveDataEnvironmentKey =
        ModuleConfiguration.EnvironmentName(AIKeys.TelemetryEnableSensitiveData);

    // The declaration API is the typed AIOptions extension family (WithModel, WithRole, WithoutRole,
    // WithoutLocalModels): carried by configuration and resolved once at bind in ConfigureDeclaration.
    private static AIHostingState State(IntoChatOSModuleBuilder<AIModuleHosting> module)
    {
        ArgumentNullException.ThrowIfNull(module);
        return module.GetOrAddProjection(brain => new AIHostingState(brain, module.Resource));
    }
}
