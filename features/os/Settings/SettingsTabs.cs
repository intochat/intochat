// The Settings tabs in order, and the tab an old section name opens (as R2 mapped them).
public static class SettingsTabs
{
    public static readonly (string Id, string Label)[] All =
    [
        ("general", "General"),
        ("models", "AI models"),
        ("keys", "API keys"),
        ("connections", "Connections"),
        ("variables", "Variables and secrets"),
        ("notifications", "Notifications"),
        ("archived", "Archived chats"),
        ("danger", "Danger zone"),
    ];

    // The Account tab is not one of the eight: it follows them when the identity provider contributes a surface.
    public const string AccountId = "account";
    public const string AccountLabel = "Account";

    public static string Id(string? section)
    {
        var id = section switch
        {
            "profile" or "appearance" or "developer" or "gallery" or "ui-kit" or "advanced" => "general",
            "agents" => "models",
            _ => section,
        };
        return id == AccountId || All.Any(tab => tab.Id == id) ? id! : "general";
    }

    public static string Label(string id) => id == AccountId ? AccountLabel : All.First(tab => tab.Id == id).Label;

    public static string Description(string id) => id switch
    {
        "general" => "Personal preferences for all projects on this device.",
        "models" => "Models this brain can use. Add a provider key in API keys to unlock more.",
        "keys" => "Keys are stored sealed for this brain. Once saved, a key is never shown again.",
        "connections" => "Accounts and services this brain can reach.",
        "variables" => "Values your brain's apps and agents can use. A secret is never shown again once saved.",
        "notifications" => "Choose what this brain tells you about.",
        "archived" => "Conversations you have archived.",
        "account" => "Your sign-in, profile and two-step verification.",
        _ => "Actions that change access to your account.",
    };
}
