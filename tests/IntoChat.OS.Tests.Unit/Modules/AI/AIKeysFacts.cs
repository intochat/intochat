using IntoChat.OS.Modules.AI.Models;

namespace IntoChat.OS.Modules.AI.Tests.Unit;

public sealed class AIKeysFacts
{
    [Fact]
    public void EveryKeyLivesUnderTheModuleOptionsSection()
    {
        string[] keys =
        [
            AIKeys.Roles, AIKeys.LegacyDefault,
            AIKeys.Role(Where.Device, Role.Chat), AIKeys.Role(Where.Cloud, Role.Embed),
            AIKeys.Ollama, AIKeys.OllamaEndpoint, AIKeys.OllamaModels, AIKeys.TavilyEnabled, AIKeys.TelemetryEnableSensitiveData,
        ];

        Assert.False(string.IsNullOrWhiteSpace(AIKeys.Options));
        Assert.All(keys, key => Assert.StartsWith(AIKeys.Options + ":", key, StringComparison.Ordinal));
    }

    [Fact]
    public void KeysAreDistinct()
    {
        string[] keys =
        [
            AIKeys.Roles, AIKeys.LegacyDefault,
            AIKeys.Role(Where.Device, Role.Chat), AIKeys.Role(Where.Cloud, Role.Chat),
            AIKeys.Role(Where.Cloud, Role.Embed), AIKeys.Role(Where.Device, Role.Hear),
            AIKeys.Ollama, AIKeys.OllamaEndpoint, AIKeys.OllamaModels, AIKeys.TavilyEnabled, AIKeys.TelemetryEnableSensitiveData,
        ];

        Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void TheOllamaKeysNestUnderTheOllamaSection()
    {
        Assert.Equal(AIKeys.Ollama + ":Endpoint", AIKeys.OllamaEndpoint);
        Assert.Equal(AIKeys.Ollama + ":Models", AIKeys.OllamaModels);
    }

    [Fact]
    public void RoleKeysNameTheirMapAndRole()
    {
        Assert.EndsWith(":Roles:Device:Chat", AIKeys.Role(Where.Device, Role.Chat), StringComparison.Ordinal);
        Assert.EndsWith(":Roles:Cloud:Embed", AIKeys.Role(Where.Cloud, Role.Embed), StringComparison.Ordinal);
        Assert.EndsWith(":Default", AIKeys.LegacyDefault, StringComparison.Ordinal);
    }
}
