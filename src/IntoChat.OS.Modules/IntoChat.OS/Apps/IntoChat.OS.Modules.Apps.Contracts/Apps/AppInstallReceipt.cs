namespace IntoChat.OS.Apps;

[GenerateSerializer, Alias("apps.install-receipt")]
public sealed record AppInstallReceipt(
    [property: Id(0)] Guid OperationId,
    [property: Id(1)] AppPublicationRef Publication,
    [property: Id(2)] string TargetName,
    [property: Id(3)] string AccountId);
