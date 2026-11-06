namespace IntoChat.OS.Modules.Apps;

// What an Ino tool hands back to the agent: text for the model, plus an optional card for the Ino surface.
public sealed record InoToolResult(string Text, InoCard? Card = null);
