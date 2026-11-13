using IntoChat.OS.Modules.Flutter;

namespace IntoChat.OS.Modules.Apps;

public sealed record Sphere(string Icon, string[] BlackboardKeys);

public sealed record Presentation(Sphere? Sphere = null, ViewRef? Side = null, ViewRef? Main = null,
    PresentationView[]? Views = null);

public sealed record PresentationView(string Id, ViewRef Root);

public sealed record AppPresentation(
    long ProgramRevision, long Revision,
    Presentation? Root, string? Error,
    Dictionary<string, string> Blackboard);
