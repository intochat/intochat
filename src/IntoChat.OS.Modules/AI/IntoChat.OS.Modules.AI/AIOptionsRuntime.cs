using IntoChat.OS.Framework;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
namespace IntoChat.OS.AI;

internal static class AIOptionsRuntime
{
    internal static AIOptions Read(IConfiguration configuration)
    {
        var options = new AIOptions();
        ReadInto(options, configuration);
        return options;
    }

    private static void ReadInto(AIOptions options, IConfiguration configuration)
    {
        configuration.GetSection(AIKeys.Options).Bind(options);
        options.Validate();
        ProjectOllamaModelMarkers(options, configuration);
    }

    internal static void Register(IServiceCollection services)
    {
        services.AddOptions<AIOptions>().Configure<IConfiguration>(ReadInto);
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
