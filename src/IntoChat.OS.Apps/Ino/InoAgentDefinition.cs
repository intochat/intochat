using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Memory;
using IntoChat.OS.Modules.AI.Routing;
using IntoChat.OS.Modules.AI.Tasks;
using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Apps.Ino;

// What Ino's agent is: the persona, the tool contracts it may act on, and the contexts it reads every turn.
// Context keys are brain-relative ("context/{name}"): the agent resolves them in its own brain.
public static class InoAgentDefinition
{
    private static readonly string Persona = LoadPersona();

    public static AgentDefinition For(UserSettingsState settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new()
        {
            DisplayName = "Ino",
            Description = "The brain's personal assistant.",
            Instructions = Persona.Replace("{user}", settings.DisplayName, StringComparison.Ordinal).Trim(),
            Contracts =
            [
                typeof(IInoStudio).FullName!, typeof(IInoData).FullName!, typeof(IInoSettings).FullName!, typeof(IInoFiles).FullName!, typeof(ISynapseStudio).FullName!,
                typeof(ITaskEngine).FullName!, typeof(IMemory).FullName!, typeof(IRouter).FullName!,
            ],
            Contexts =
            [
                .. new[] { "user.profile", "conversation.inference.tone", "conversation.summary", "conversation.inference", "apps.capabilities", "security.posture", "ui.focus", "memory.recall" }
                    .Select(name => "context/" + name),
            ],
        };
    }

    private static string LoadPersona()
    {
        using var stream = typeof(InoAgentDefinition).Assembly.GetManifestResourceStream("IntoChat.OS.Apps.Ino.ino-instructions.md")
            ?? throw new InvalidOperationException("Ino's instructions are not embedded.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
