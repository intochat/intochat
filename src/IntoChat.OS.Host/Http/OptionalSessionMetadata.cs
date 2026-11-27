namespace IntoChat.OS.Host.Http;

/// <summary>
/// Endpoint metadata indicating that an <c>AllowAnonymous</c> endpoint (such as <c>/ai/talk</c>
/// with single-use opaque ticket auth) still wants an existing session cookie, Basic credential,
/// or local presence proof stamped onto <c>ICallerAmbient</c> when present, without rejecting
/// requests that authenticate via their own ticket mechanism.
/// </summary>
public sealed class OptionalSessionMetadata
{
    public static readonly OptionalSessionMetadata Instance = new();
}
