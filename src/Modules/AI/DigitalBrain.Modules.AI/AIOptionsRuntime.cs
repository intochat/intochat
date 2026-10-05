using DigitalBrain.Kernel;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
namespace DigitalBrain.AI;

internal static class AIOptionsRuntime
{
    // The pre-grammar roots; configuration still carrying them is a deployment bug, not a fallback.
    private static readonly (string Legacy, string Target)[] RetiredSections =
    [
        ("DigitalBrain:AI", AIKeys.Options),
        ("DigitalBrain:Workspace", AIKeys.Workspace),
    ];

    internal static AIOptions Read(IConfiguration configuration)
    {
        var options = new AIOptions();
        ReadInto(options, configuration);
        return options;
    }

    private static void ReadInto(AIOptions options, IConfiguration configuration)
    {
        foreach (var (legacy, target) in RetiredSections)
        {
            if (configuration.GetSection(legacy).Exists())
            { throw new InvalidOperationException($"'{legacy}' moved to '{target}' — migrate the keys."); }
        }
        configuration.GetSection(AIKeys.Options).Bind(options);
        options.Validate();
        ProjectOllamaModelMarkers(options, configuration);
    }

    internal static void Register(IServiceCollection services)
    {
        services.AddOptions<AIOptions>().Configure<IConfiguration>(ReadInto);
        services.AddOptions<AIWorkspaceOptions>().BindConfiguration(AIKeys.Workspace);
    }

    private static void ProjectOllamaModelMarkers(AIOptions options, IConfiguration configuration)
    {
        foreach (var section in configuration.GetSection(AIKeys.Ollama).GetChildren())
        {
            if (section["Model"] is { } model)
            {
                options.Ollama.Models[section.Key] = new AIModelOptions { Model = model };
            }
        }
    }
}
