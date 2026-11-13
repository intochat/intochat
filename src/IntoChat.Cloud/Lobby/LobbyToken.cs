using System.Security.Cryptography;
using System.Text;

namespace IntoChat.Cloud.Lobby;

public static class LobbyToken
{
    public const string CookieName = "intochat_lobby";
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    public static string Issue(byte[] key, string visitorId, string ipPrefix, DateTimeOffset expiresAt)
    {
        var payload = Payload(visitorId, ipPrefix, expiresAt);
        return Base64Url(Encoding.UTF8.GetBytes(payload)) + "." + Sign(key, payload);
    }

    public static bool TryRead(byte[] key, string? token, DateTimeOffset now, out string visitorId, out string ipPrefix)
    {
        visitorId = "";
        ipPrefix = "";
        if (string.IsNullOrEmpty(token)) { return false; }
        var dot = token.LastIndexOf('.');
        if (dot <= 0 || dot == token.Length - 1) { return false; }
        string payload;
        try { payload = Encoding.UTF8.GetString(Base64UrlDecode(token[..dot])); }
        catch (FormatException) { return false; }
        var mac = token[(dot + 1)..];
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(mac), Encoding.ASCII.GetBytes(Sign(key, payload)))) { return false; }
        var parts = payload.Split('|');
        if (parts.Length != 3 || !long.TryParse(parts[2], out var unix)) { return false; }
        if (DateTimeOffset.FromUnixTimeSeconds(unix) <= now) { return false; }
        visitorId = parts[0];
        ipPrefix = parts[1];
        return visitorId.Length > 0 && ipPrefix.Length > 0;
    }

    public static string SignState(byte[] key, string visitorId, string nonce, DateTimeOffset expiresAt)
    {
        var payload = visitorId + "|" + nonce + "|" + expiresAt.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Base64Url(Encoding.UTF8.GetBytes(payload)) + "." + Sign(key, payload);
    }

    public static bool StateMatches(byte[] key, string? state, string visitorId, string nonce, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(state)) { return false; }
        var dot = state.LastIndexOf('.');
        if (dot <= 0) { return false; }
        string payload;
        try { payload = Encoding.UTF8.GetString(Base64UrlDecode(state[..dot])); }
        catch (FormatException) { return false; }
        var mac = state[(dot + 1)..];
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(mac), Encoding.ASCII.GetBytes(Sign(key, payload)))) { return false; }
        var parts = payload.Split('|');
        if (parts.Length != 3 || parts[0] != visitorId || parts[1] != nonce || !long.TryParse(parts[2], out var unix)) { return false; }
        return DateTimeOffset.FromUnixTimeSeconds(unix) > now;
    }

    public static string PrefixOf(string? remoteIp)
    {
        if (string.IsNullOrWhiteSpace(remoteIp)) { return "none"; }
        if (System.Net.IPAddress.TryParse(remoteIp, out var address) && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var text = address.ToString();
            var last = text.LastIndexOf('.');
            return last > 0 ? text[..last] : text;
        }
        return remoteIp.Length <= 19 ? remoteIp : remoteIp[..19];
    }

    private static string Payload(string visitorId, string ipPrefix, DateTimeOffset expiresAt) =>
        visitorId + "|" + ipPrefix + "|" + expiresAt.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string Sign(byte[] key, string payload)
    {
        var mac = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexStringLower(mac);
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string text)
    {
        var padded = text.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
        return Convert.FromBase64String(padded);
    }
}

public sealed class LobbyVisitorBudget
{
    private readonly Dictionary<string, List<(string Visitor, DateTimeOffset Seen)>> admitted = [];
    private readonly Lock gate = new();

    public bool TryAdmit(string prefix, string visitorId, DateTimeOffset now, int cap, TimeSpan idle)
    {
        lock (gate)
        {
            if (!admitted.TryGetValue(prefix, out var rows)) { admitted[prefix] = rows = []; }
            rows.RemoveAll(row => now - row.Seen > idle);
            if (rows.Any(row => row.Visitor == visitorId))
            {
                var index = rows.FindIndex(row => row.Visitor == visitorId);
                rows[index] = (visitorId, now);
                return true;
            }
            if (rows.Count >= cap) { return false; }
            rows.Add((visitorId, now));
            return true;
        }
    }
}
