using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Apps.Ino;

public sealed record InoErrorLine(
    string What,
    string? Detail,
    string? ActionLabel,
    string? ActionKey,
    string? Open,
    bool Retry);

public static class InoErrors
{
    public const string ErrorActionKey = "error-action";
    public const string RetryKey = "retry";

    public static InoErrorLine Of(InoError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error.Code switch
        {
            "no-provider" => OpenLine("Ino has no model to answer with.", "Set up a model", "models"),
            "provider-rejected-key" => OpenLine("The model provider refused the key.", "Open API keys", "keys"),
            "provider-unavailable" or "timed-out" => RetryLine("The model provider did not answer."),
            "budget" => OpenLine("The monthly limit is reached.", "Open billing", "billing"),
            "paused" => new("Ino is paused.", error.Detail, null, null, null, Retry: false),
            "too-long" => new("That message is too long.", null, null, null, null, Retry: false),
            "interrupted" => RetryLine("Ino was interrupted."),
            "cancelled" => RetryLine("Stopped."),
            _ => RetryLine("Ino could not answer."),
        };
    }

    /// <summary>What a failed reply's failure code means to the user; null for no code (the thread then says the model did not answer).</summary>
    public static string? Describe(string? failure)
        => string.IsNullOrWhiteSpace(failure) ? null : Of(new InoError(failure.Trim())).What;

    private static InoErrorLine OpenLine(string what, string action, string section)
        => new(what, null, action, ErrorActionKey, $"os://window/settings?section={section}", Retry: false);

    private static InoErrorLine RetryLine(string what)
        => new(what, null, "Try again", RetryKey, null, Retry: true);
}
