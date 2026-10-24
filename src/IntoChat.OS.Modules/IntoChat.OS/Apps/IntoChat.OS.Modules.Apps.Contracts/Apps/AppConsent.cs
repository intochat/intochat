using System.Security.Cryptography;
using System.Text;

namespace IntoChat.OS.Apps;

// What a person accepted, as a digest of the requirements they were shown: install and update compare it with the
// bundle's requirements at the moment they apply, so a release that changed in between installs nothing.
public static class AppConsent
{
    public static string Of(AppRequirements? requires)
    {
        var text = "s:" + Joined(requires?.SemanticTypes) + "\nc:" + Joined(requires?.Connectors) + "\nx:" + Joined(requires?.Secrets);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    // What `to` asks for that `from` did not, per list.
    public static AppRequirements Added(AppRequirements? from, AppRequirements? to)
        => new(Except(to?.SemanticTypes, from?.SemanticTypes), Except(to?.Connectors, from?.Connectors), Except(to?.Secrets, from?.Secrets));

    public static bool IsEmpty(AppRequirements? requires)
        => requires is null || (requires.SemanticTypes ?? []).Length + (requires.Connectors ?? []).Length + (requires.Secrets ?? []).Length == 0;

    private static string Joined(string[]? ids) => string.Join(",", (ids ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));

    private static string[] Except(string[]? to, string[]? from)
        => [.. (to ?? []).Except(from ?? [], StringComparer.Ordinal).Order(StringComparer.Ordinal)];
}
