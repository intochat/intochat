using DigitalBrain;

namespace IntoChat.OS.Apps.Post;

[Signal("post.open")]
public sealed record PostOpen : Signal;

[Signal("post.publish")]
public sealed record PostPublish(string? Content = null, string Circle = "public", string? Id = null) : Signal;

[Signal("post.update-bio")]
public sealed record PostUpdateBio(string Handle = "user", string Bio = "", string[]? PinnedApps = null) : Signal;
