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

    public static string Id(string? section)
    {
        var id = section switch
        {
            "profile" or "appearance" or "developer" or "gallery" or "ui-kit" or "advanced" => "general",
            "agents" => "models",
            _ => section,
        };
        return All.Any(tab => tab.Id == id) ? id! : "general";
    }

    public static string Label(string id) => All.First(tab => tab.Id == id).Label;

    public static string Description(string id) => id switch
    {
        "general" => "Personal preferences for all projects on this device.",
        "models" => "Models this brain can use. Add a provider key in API keys to unlock more.",
        "keys" => "Keys are stored sealed for this brain. Once saved, a key is never shown again.",
        "connections" => "Accounts and services this brain can reach.",
        "variables" => "Values your brain's apps and agents can use. A secret is never shown again once saved.",
        "notifications" => "Choose what this brain tells you about.",
        "archived" => "Conversations you have archived.",
        _ => "Actions that change access to your account.",
    };
}
