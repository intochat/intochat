namespace IntoChat.OS.Apps.Post;

[DigitalBrain.Signal("post.open")]
public sealed record PostOpen : DigitalBrain.Signal;

[DigitalBrain.Signal("post.publish")]
public sealed record PostPublish(
    string? Content = null,
    string Circle = "public",
    string? Id = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("post.update-bio")]
public sealed record PostUpdateBio(
    string Handle = "user",
    string Bio = "",
    string[]? PinnedApps = null) : DigitalBrain.Signal;
