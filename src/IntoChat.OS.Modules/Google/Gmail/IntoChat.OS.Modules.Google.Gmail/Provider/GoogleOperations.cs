namespace IntoChat.OS.Google.Gmail;

internal static class GoogleOperations
{
    public const string Profile = "google.gmail.profile.get";
    public const string ListMessages = "google.gmail.messages.list";
    public const string Metadata = "google.gmail.messages.get.metadata";
    public const string Message = "google.gmail.messages.get.full";
    public const string CreateDraft = "google.gmail.drafts.create";
    public const string History = "google.gmail.history.list";
    public const string Watch = "google.gmail.watch";
    public const string Stop = "google.gmail.stop";
    public const string DriveAbout = "google.drive.about.get";
    public const string DriveList = "google.drive.files.list";
    public const string DriveMetadata = "google.drive.files.get.metadata";
    public const string DriveMedia = "google.drive.files.get.media";
    public const string DriveExport = "google.drive.files.export";
    public const string CodeExchange = "google.oauth.code.exchange";
    public const string Refresh = "google.oauth.token.refresh";
}
