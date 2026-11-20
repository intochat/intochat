using DigitalBrain.Access;

namespace IntoChat.OS.Modules.Identity.Grants;

public sealed record PrincipalSet(string[] Principals)
{
    public static PrincipalSet For(Caller caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return For(caller.Brain.Value, GrantRules.AppOf(caller));
    }

    private static PrincipalSet For(string brainId, string? appId)
    {
        var list = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(brainId))
        {
            list.Add("brain:" + brainId);
        }
        if (!string.IsNullOrWhiteSpace(appId))
        {
            list.Add("app:" + appId);
        }
        list.Add("public");
        return new PrincipalSet([.. list]);
    }

    public PrincipalSet With(params string[] usersets)
    {
        if (usersets is null || usersets.Length == 0)
        {
            return this;
        }
        var existing = new HashSet<string>(Principals, StringComparer.Ordinal);
        foreach (var u in usersets)
        {
            if (!string.IsNullOrWhiteSpace(u))
            {
                existing.Add(u);
            }
        }
        return new PrincipalSet([.. existing]);
    }
}
