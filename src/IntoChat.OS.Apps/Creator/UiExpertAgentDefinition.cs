using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Memory;
using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Apps.Creator;

/// <summary>
/// Specialized agent definition for UI creation, component styling, and layout building.
/// </summary>
public static class UiExpertAgentDefinition
{
    private static readonly string Persona = LoadPersona();

    public static AgentDefinition For(UserSettingsState settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new()
        {
            DisplayName = "UI Expert",
            Description = "Specialized UI builder and design system expert for IntoChat OS.",
            Instructions = Persona.Replace("{user}", settings.DisplayName, StringComparison.Ordinal).Trim(),
            Contracts =
            [
                typeof(IShellControl).FullName!,
                typeof(IInoFocus).FullName!,
                typeof(IInoFiles).FullName!,
                typeof(IMemory).FullName!,
            ],
            Contexts =
            [
                .. new[] { "user.profile", "conversation.inference.tone", "apps.capabilities", "ui.focus", "ui.state" }
                    .Select(name => "context/" + name),
            ],
        };
    }

    private static string LoadPersona()
    {
        using var stream = typeof(UiExpertAgentDefinition).Assembly.GetManifestResourceStream("IntoChat.OS.Apps.Creator.ui-expert-instructions.md")
            ?? throw new InvalidOperationException("UI Expert's instructions are not embedded.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
