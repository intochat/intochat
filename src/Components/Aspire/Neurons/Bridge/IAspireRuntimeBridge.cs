namespace IntoChat.Aspire.Bridge;

internal interface IAspireRuntimeBridge
{
    Task<BridgeCommandReply> ExecuteAsync(string instanceId, string command, TimeSpan timeout, CancellationToken ct);
    Task<string[]> ConsoleLogAsync(string instanceId, int tail, TimeSpan timeout, CancellationToken ct);
}
