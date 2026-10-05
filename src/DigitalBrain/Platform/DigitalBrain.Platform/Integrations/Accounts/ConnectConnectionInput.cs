namespace DigitalBrain.Platform.Integrations.Accounts;
internal sealed record ConnectConnectionInput(string? ConnectorId, string? ConnectionId, string? Label, string? Value, string? SecretReference, string? AuthMethodId);
