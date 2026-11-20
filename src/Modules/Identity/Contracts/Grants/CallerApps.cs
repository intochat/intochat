namespace IntoChat.OS.Modules.Identity.Contracts.Grants;

// The first-party app keys grants are recorded under. Grant subjects only: a caller's identity is its stamped Actor.
public static class CallerApps
{
    // The assistant every Actor.Agent is granted as.
    public const string Assistant = "assistant";
}
