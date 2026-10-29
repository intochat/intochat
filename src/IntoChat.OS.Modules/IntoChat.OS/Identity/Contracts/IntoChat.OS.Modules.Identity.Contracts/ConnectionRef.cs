namespace IntoChat.OS.Integrations;

// An opaque account. Kind and id name the credential the platform holds; the value never crosses this type.
public sealed record ConnectionRef(string Kind, string Id);
