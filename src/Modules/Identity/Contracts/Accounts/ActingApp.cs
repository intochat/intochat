using IntoChat.OS.Modules.Identity.Contracts.Grants;

namespace IntoChat.OS.Modules.Identity.Contracts.Accounts;

// A person proven at the edge may ask to act as one of the OS's own edge apps instead of as themself: the call then carries
// Actor.App(key, person) and only that app's grants. It only narrows: a guest or an app caller is never changed, and no other key
// is accepted, so the header can never name a module, the kernel, or an app whose grants are wider than the person's intent.
public static class ActingApp
{
    public const string HeaderName = "X-IntoChat-App";

    public static bool IsEdgeApp(string? key) => string.Equals(key, ShippedGrants.CliApp, StringComparison.Ordinal);
}
