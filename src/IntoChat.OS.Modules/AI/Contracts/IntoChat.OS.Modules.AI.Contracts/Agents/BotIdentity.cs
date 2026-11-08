using System;

namespace IntoChat.OS.Modules.AI.Agents;

public static class BotIdentity
{
    public const string Login = "intochat-bot[bot]";

    public static bool IsBot(string? author)
    {
        if (string.IsNullOrWhiteSpace(author))
        {
            return false;
        }

        return string.Equals(author, Login, StringComparison.OrdinalIgnoreCase)
            || author.EndsWith("[bot]", StringComparison.OrdinalIgnoreCase)
            || string.Equals(author, "github-actions[bot]", StringComparison.OrdinalIgnoreCase);
    }
}
