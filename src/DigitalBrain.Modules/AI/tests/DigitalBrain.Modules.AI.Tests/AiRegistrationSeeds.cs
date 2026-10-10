using DigitalBrain.Framework.Platform.Integrations;
using DigitalBrain.Framework.Platform.Secrets;
using DigitalBrain.Platform.Secrets;
using DigitalBrain.Testing;
using DigitalBrain.Testing.Module;

namespace DigitalBrain.Modules.AI.Tests;

internal static class AiRegistrationSeeds
{
    internal const string OpenAIDefaultEndpoint = "https://api.openai.com/v1";

    internal static TestExecutionOptions OpenAI(string apiKey = "test-only", string endpoint = OpenAIDefaultEndpoint)
        => new()
        {
            PrivateConfiguration = new Dictionary<string, string?>
            {
                [IntegrationSchema.Key("openai", "ApiKey")] = apiKey,
                [IntegrationSchema.Key("openai", "Endpoint")] = endpoint,
            },
        };

    internal static TestBrain WithRegistrations(this TestBrain builder, TestExecutionOptions? seeds = null)
        => builder.WithExecution(seeds ?? new TestExecutionOptions());
}
