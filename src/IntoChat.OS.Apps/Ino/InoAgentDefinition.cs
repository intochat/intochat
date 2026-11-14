using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Memory;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.AI.Routing;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.Apps.Trace;
using IntoChat.OS.Modules.Microsoft.Aspire;

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
            Model = string.IsNullOrWhiteSpace(settings.DefaultModel) ? null : new ModelSelection(settings.DefaultModel.Trim()),
            Contracts =
            [
                typeof(IInoStudio).FullName!, typeof(IInoData).FullName!, typeof(IInoSettings).FullName!, typeof(IInoFiles).FullName!, typeof(ISynapseStudio).FullName!, typeof(IInoFocus).FullName!,
                typeof(IMemory).FullName!, typeof(IRouter).FullName!, typeof(IAspire).FullName!, typeof(ITraceReader).FullName!, typeof(IFlows).FullName!,
            ],
            Contexts =
            [
                .. new[] { "user.profile", "conversation.inference.tone", "conversation.summary", "conversation.inference", "apps.capabilities", "security.posture", "ui.focus", "ui.state", "memory.recall" }
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
