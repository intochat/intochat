using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Modules.Circles;

public sealed record CirclePublicationFeed(
    AppPublication[] Items,
    string[] UnavailablePublishers,
    string PrincipalId);
