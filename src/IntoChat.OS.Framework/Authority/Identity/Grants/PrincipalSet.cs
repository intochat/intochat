using DigitalBrain.Capabilities;

namespace IntoChat.OS.Authority.Identity.Grants;

public sealed record PrincipalSet(string[] Principals)
{
    public static PrincipalSet For(CapabilityCaller caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var list = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(caller.BrainId))
        {
            list.Add("brain:" + caller.BrainId);
        }
        if (!string.IsNullOrWhiteSpace(caller.AppId))
        {
            list.Add("app:" + caller.AppId);
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
