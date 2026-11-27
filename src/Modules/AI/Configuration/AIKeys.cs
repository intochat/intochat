using DigitalBrain.Modules.Configuration;
using DigitalBrain.Modules.Options;
using IntoChat.OS.Modules.AI.Models;

namespace IntoChat.OS.Modules.AI;

// Every AI key is derived from the one configuration grammar; no private roots.
public static class AIKeys
{
    public static readonly string Options = ModuleConfiguration.ModuleOptionsSection(new ModuleId("ai"));
    public static readonly string Roles = Options + ":Roles";
    public static readonly string LegacyDefault = Options + ":Default";
    public static readonly string Ollama = Options + ":Ollama";
    public static readonly string OllamaEndpoint = Ollama + ":Endpoint";
    public static readonly string OllamaModels = Ollama + ":Models";
    public static readonly string AzureFoundry = Options + ":AzureFoundry";
    public static readonly string AzureFoundryEndpoint = AzureFoundry + ":Endpoint";
    public static readonly string AzureFoundryUseEntraId = AzureFoundry + ":UseEntraId";
    public static readonly string TelemetryEnableSensitiveData = Options + ":Telemetry:EnableSensitiveData";

    public static string Role(Where where, Models.ModelRole role)
        => $"{Roles}:{(where == Where.Device ? "Device" : "Cloud")}:{role}";
}
