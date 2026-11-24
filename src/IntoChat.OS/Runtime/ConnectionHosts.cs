// os-only: the http@1 allow-list port between the kernel's http connector and the build flow's slot binding
namespace IntoChat.OS;

// The hosts a brain's connection may reach: an http@1 connection's allow-list (exact hosts or "*.example.org"), or null when the
// connection keeps none (not an http connection, another brain's, or unknown). The kernel implements it over the connection's own
// state; the build flow binds a unit's http slot only to a connection whose list covers every host the slot needs.
public interface IConnectionHosts
{
    Task<IReadOnlyList<string>?> AllowedHosts(DigitalBrain.BrainId brain, string connectionKey, CancellationToken ct);

    // Whether allowed covers host: an exact entry (case-insensitive), or a "*.suffix" entry for any host below suffix (never suffix
    // itself). The same rule DigitalBrain's HttpHosts.Require applies to each request.
    static bool Covers(IReadOnlyList<string> allowed, string host)
    {
        ArgumentNullException.ThrowIfNull(allowed);
        ArgumentNullException.ThrowIfNull(host);
        return allowed.Any(entry => entry.StartsWith("*.", StringComparison.Ordinal)
            ? host.Length > entry.Length - 1 && host.EndsWith(entry[1..], StringComparison.OrdinalIgnoreCase)
            : string.Equals(entry, host, StringComparison.OrdinalIgnoreCase));
    }

    // The allow-list text of an http connection: comma-separated hosts or a JSON array of strings; blanks dropped.
    // Read through JsonDocument, not JsonSerializer, so the contract needs no reflection metadata (trim/AOT-safe).
    static string[] Parse(string? text)
    {
        var trimmed = (text ?? "").Trim();
        if (trimmed.StartsWith('['))
        {
            using var json = System.Text.Json.JsonDocument.Parse(trimmed);
            return [.. json.RootElement.EnumerateArray().Select(entry => (entry.GetString() ?? "").Trim()).Where(host => host.Length > 0)];
        }
        return [.. trimmed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
    }
}

// An http@1 connection's own face beyond IHttpRead: the allow-list it was connected with.
[DigitalBrain.Contracts.NeuronAlias("http.connection"), DigitalBrain.Contracts.DefaultNeuronType(TypeName)]
public interface IHttpConnection
{
    const string TypeName = "http";

    [DigitalBrain.Contracts.ReadOnlyCall]
    Task<string[]> AllowedHosts();
}
