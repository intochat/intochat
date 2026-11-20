using DigitalBrain.Modules.Configuration;
using DigitalBrain.Modules.Options;
using IntoChat.OS;
using IntoChat.OS.Connections;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Host.Configuration;

namespace IntoChat.OS.Modules.AI;

// Every AI key is derived from the one configuration grammar; no private roots.
public static class AIKeys
{
    public static readonly string Options = ModuleConfiguration.ModuleOptionsSection(new ModuleId("ai"));
    public static readonly string DefaultModel = Options + ":Default:Model";
    public static readonly string DefaultEmbedding = Options + ":Default:Embedding";
    public static readonly string DefaultTranscription = Options + ":Default:Transcription";
    public static readonly string DefaultImage = Options + ":Default:Image";
    public static readonly string DefaultSystemOne = Options + ":Default:SystemOne";
    public static readonly string Ollama = Options + ":Ollama";
    public static readonly string OllamaEndpoint = Ollama + ":Endpoint";
    public static readonly string OllamaModels = Ollama + ":Models";
    public static readonly string TavilyEnabled = Options + ":Tavily:Enabled";
    public static readonly string TelemetryEnableSensitiveData = Options + ":Telemetry:EnableSensitiveData";
}
