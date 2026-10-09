namespace DigitalBrain.Framework.Platform.Connections;

// A connection is keyed {brain}/connection/{connector}/{id}; the brain id carries no '/'.
public static class ConnectionKeys
{
    private const string Segment = "connection";

    public static bool Is(string key) => key.Split('/') is [_, Segment, _, _];

    public static string For(string brainId, string connectorId, string connectionId)
    {
        if (!BrainScope.IsValidId(brainId)) { throw new ArgumentException("A brain ID is required.", nameof(brainId)); }
        ArgumentException.ThrowIfNullOrWhiteSpace(connectorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        return $"{brainId}/{Segment}/{Uri.EscapeDataString(connectorId)}/{Uri.EscapeDataString(connectionId)}";
    }

    public static (string BrainId, string ConnectorId, string ConnectionId) Parse(string key)
    {
        if (key.Split('/') is not [var brain, Segment, var connector, var connection] || !BrainScope.IsValidId(brain))
        { throw new ArgumentException("A connection grain key is required.", nameof(key)); }
        var (connectorId, connectionId) = (Uri.UnescapeDataString(connector), Uri.UnescapeDataString(connection));
        if (For(brain, connectorId, connectionId) != key) { throw new ArgumentException("A canonical connection key is required.", nameof(key)); }
        return (brain, connectorId, connectionId);
    }
}
