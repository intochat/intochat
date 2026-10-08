namespace DigitalBrain.Framework.Platform.Connections;

public static class ConnectionKeys
{
    public const string Prefix = "connection/";
    public static bool Is(string key) => key.StartsWith(Prefix, StringComparison.Ordinal);

    public static string For(string brainId, string connectorId, string connectionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brainId);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        return $"connection/{Uri.EscapeDataString(brainId)}/{Uri.EscapeDataString(connectorId)}/{Uri.EscapeDataString(connectionId)}";
    }

    public static (string BrainId, string ConnectorId, string ConnectionId) Parse(string key)
    {
        if (key.Split('/') is not ["connection", var brain, var connector, var connection])
        { throw new ArgumentException("A connection grain key is required.", nameof(key)); }
        var result = (Uri.UnescapeDataString(brain), Uri.UnescapeDataString(connector), Uri.UnescapeDataString(connection));
        if (For(result.Item1, result.Item2, result.Item3) != key) { throw new ArgumentException("A canonical connection key is required.", nameof(key)); }
        return result;
    }
}
