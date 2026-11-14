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
            "no-provider" => new(
                "Ino has no model to answer with.",
                null,
                "Set up a model",
                ErrorActionKey,
                "os://window/settings?section=models",
                Retry: false),
            "provider-rejected-key" => new(
                "The model provider refused the key.",
                null,
                "Open API keys",
                ErrorActionKey,
                "os://window/settings?section=keys",
                Retry: false),
            "provider-unavailable" or "timed-out" => new(
                "The model provider did not answer.",
                null,
                "Try again",
                RetryKey,
                null,
                Retry: true),
            "budget" => new(
                "The monthly limit is reached.",
                null,
                "Open billing",
                ErrorActionKey,
                "os://window/settings?section=billing",
                Retry: false),
            "paused" => new(
                "Ino is paused.",
                error.Detail,
                null,
                null,
                null,
                Retry: false),
            "too-long" => new(
                "That message is too long.",
                null,
                null,
                null,
                null,
                Retry: false),
            "interrupted" => new(
                "Ino was interrupted.",
                null,
                "Try again",
                RetryKey,
                null,
                Retry: true),
            "tool-failed" or "unknown" or _ => new(
                "Ino could not answer.",
                null,
                "Try again",
                RetryKey,
                null,
                Retry: true),
        };
    }
}
