using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using DigitalBrain;
using DigitalBrain.AI.Models.FoundryLocal;
using DigitalBrain.Aspire.Hosting;
using DigitalBrain.Framework.Configuration;

namespace DigitalBrain.AI.Aspire.Hosting;

public static partial class AIHostingExtensions
{
    private static readonly string EnableSensitiveDataEnvironmentKey =
        DigitalBrainConfiguration.EnvironmentName(AIKeys.TelemetryEnableSensitiveData);

    // The declaration API is the typed AIOptions extension family (WithLlm<TModel> and
    // friends): typed at the declaration site, carried by configuration — the one value
    // channel — and resolved loudly once at bind in ConfigureDeclaration.
    private static AIHostingState State(DigitalBrainModuleBuilder<DigitalBrain.AI.Aspire.Hosting.AIModuleHosting> module)
    {
        ArgumentNullException.ThrowIfNull(module);
        var state = module.GetOrAddProjection(brain => new AIHostingState(brain, module.Resource));

        return state;
    }

}
