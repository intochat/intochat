// Every string the Settings view shows, verbatim from the R5 plan, so the view and its facts share one source.
public static class SettingsCopy
{
    public const string OwnerOnlyKeys = "Only the brain owner can change API keys.";
    public const string OwnerOnlyVariables = "Only the brain owner can change variables and secrets.";
    public const string KeyNotSet = "Not set";
    public const string KeySet = "Set";
    public static string KeySetTail(string tail) => "Set ••••" + tail;
    public const string ModelUnavailable = "Not available. Add a key in API keys or pick another model.";
    public const string Unavailable = "Unavailable";
    public const string AutoRow = "Auto, cheapest model with tools";
    public const string DefaultModel = "Default model";

    public const string Saved = "Saved.";
    public const string SaveFailed = "Could not save that change.";
    public const string ChatRestored = "Chat restored.";
    public const string LinkCopied = "Link copied.";
    public const string ListChanged = "This list changed. Try again.";
    public const string OpenFailed = "Settings could not load. Reopen Settings to try again.";

    public static string ConnectionsCapped(int shown, int total) => $"Showing {shown} of {total} connections.";
    public const string Billing = "Billing · Later";
    public const string Variables = "Variables and secrets";

    public const string DisplayName = "Display name";
    public const string Theme = "Theme";
    public const string CompactSpacing = "Compact spacing";
    public const string ReducedMotion = "Reduce motion";
    public const string NewTab = "New tab opens";

    public static readonly (string Key, string Label)[] Notifications =
    [
        ("agent-finished", "An agent finishes a task"),
        ("needs-approval", "Something needs your approval"),
        ("connection-errors", "A connection stops working"),
        ("product-updates", "Product updates"),
    ];

    // In BrainEntries.KeyedProviderIds order; OsSettingsViewFacts pins the two equal.
    public static readonly (string Id, string Label)[] KeyProviders =
    [
        ("openai", "OpenAI"),
        ("anthropic", "Anthropic"),
        ("google", "Google"),
        ("xai", "xAI"),
        ("openrouter", "OpenRouter"),
    ];

    public const string Connect = "Connect";

    public const string ArchivedEmpty = "Archived chats appear here.";
    public const string Unarchive = "Unarchive";
    public const string ShowMore = "Show more";

    public const string DeleteAccount = "Delete account";
    public const string DeleteDetail = "Opens your account page, where deletion is managed.";
    public const string DeleteConfirmTitle = "Delete your account?";
    public const string DeleteConfirmDetail = "Deletion is managed on your account page.";
    public const string OpenAccountPage = "Open account page";
    public const string Cancel = "Cancel";
}
