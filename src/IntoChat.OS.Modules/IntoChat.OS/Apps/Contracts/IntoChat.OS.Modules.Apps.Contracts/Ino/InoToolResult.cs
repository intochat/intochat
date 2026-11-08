namespace IntoChat.OS.Modules.Apps;

// What an Ino tool hands back to the agent: text for the model, plus an optional card for the Ino surface.
// Ok is false when the tool could not do what was asked; Text then says why.
public sealed record InoToolResult(string Text, InoCard? Card = null, bool Ok = true);
