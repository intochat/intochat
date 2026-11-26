namespace IntoChat.OS.Modules.Apps;

public sealed record AppInstallReceipt(
    Guid OperationId,
    AppPublicationRef Publication,
    string TargetName,
    string AccountId);
