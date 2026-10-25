using Microsoft.Extensions.Configuration;

namespace IntoChat.OS.ServiceDefaults;

// Applies the bind policy to the listeners Kestrel reads from configuration (urls, ASPNETCORE_URLS, DOTNET_URLS,
// Kestrel:Endpoints) before anything binds. On local and local-cloud a listener beyond loopback needs IntoChatOS:Bind to name
// it; the policy refuses rather than rewrites, so Aspire-assigned ports stay as they are. Bare port variables are the
// Open-posture check's (OpenPostureLoopback): Aspire projects them to local executables on purpose.
public static class BindPolicyKestrel
{
    private static readonly string[] UrlKeys = ["urls", "ASPNETCORE_URLS", "DOTNET_URLS"];

    public static void Validate(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var policy = BindPolicy.From(configuration);
        if (!BindPolicy.IsLocal(configuration)) { return; }
        var offenders = Disallowed(configuration, policy);
        if (offenders.Count == 0) { return; }
        throw new InvalidOperationException(
            $"{BindPolicy.TopologyKey} is local, so listeners bind loopback only unless {BindPolicy.BindKey} names the address. Not allowed: "
            + string.Join(", ", offenders) + $". Bind 127.0.0.1/localhost, or set {BindPolicy.BindKey} (0.0.0.0 or :: for every interface).");
    }

    public static IReadOnlyList<string> Disallowed(IConfiguration configuration, BindPolicy policy)
    {
        var found = new List<string>();
        foreach (var key in UrlKeys)
        {
            foreach (var url in (configuration[key] ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            { if (!policy.Allows(Host(url))) { found.Add(url); } }
        }
        foreach (var endpoint in configuration.GetSection("Kestrel:Endpoints").GetChildren())
        {
            if (endpoint["Url"] is { Length: > 0 } url && !policy.Allows(Host(url))) { found.Add(url); }
        }
        return found.Distinct(StringComparer.Ordinal).ToArray();
    }

    public static string Host(string url)
    {
        var rest = url.Trim();
        var scheme = rest.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) { rest = rest[(scheme + 3)..]; }
        var slash = rest.IndexOf('/');
        if (slash >= 0) { rest = rest[..slash]; }
        if (rest.StartsWith('[')) { var close = rest.IndexOf(']'); return close > 0 ? rest[1..close] : rest; }
        var colon = rest.LastIndexOf(':');
        return colon >= 0 ? rest[..colon] : rest;
    }
}
