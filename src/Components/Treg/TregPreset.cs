using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Mcp;

namespace IntoChat.Treg;

/// <summary>
/// Pure MCP connection presets for the hosted <c>treg.to</c> tool registry.
/// Carries endpoint metadata, verified auth header scheme, allowed tool lists, and confirmation defaults;
/// the person's bearer token is never stored here and is sealed only when connecting the MCP neuron.
/// </summary>
public static class TregPreset
{
    public const string ConnectionName = "treg";
    public const string SlotName = "treg";
    public const string CapabilityId = "tool@1";

    /// <summary>
    /// Standard treg MCP endpoint (supports curated catalog, team tools, imported skills, and media calls).
    /// Authenticated via <c>Authorization: Bearer &lt;token&gt;</c> or OAuth grant.
    /// </summary>
    public const string StandardEndpoint = "https://treg.to/mcp/";

    /// <summary>
    /// Restricted treg MCP v2 endpoint (curated catalog only; excludes team tools, imported skills, and media generation).
    /// Authenticated via <c>Authorization: Bearer &lt;token&gt;</c> or OAuth grant.
    /// </summary>
    public const string RestrictedV2Endpoint = "https://treg.to/mcp/v2/";

    public const string AuthHeaderName = "Authorization";
    public const string AuthHeaderPrefix = "Bearer ";

    private static readonly string[] StandardTools =
    [
        "catalog_search",
        "catalog_get",
        "call",
        "call_result",
        "balance",
    ];

    private static readonly string[] RestrictedV2Tools =
    [
        "catalog_search",
        "catalog_get",
        "catalog_call_read",
        "catalog_call_write",
        "balance",
    ];

    public static IReadOnlyList<string> StandardAllowedTools => StandardTools;

    public static IReadOnlyList<string> RestrictedV2AllowedTools => RestrictedV2Tools;

    /// <summary>
    /// Returns the default <see cref="McpConnectionConfig"/> targeting <see cref="StandardEndpoint"/>
    /// with verified tool names (<c>catalog_search</c>, <c>catalog_get</c>, <c>call</c>, <c>call_result</c>, <c>balance</c>)
    /// and <see cref="McpConfirmationMode.OncePerSession"/> enabled by default because treg tool calls are paid per call.
    /// </summary>
    public static McpConnectionConfig Default(
        McpConfirmationMode confirmationMode = McpConfirmationMode.OncePerSession,
        string? title = null)
        => new(
            Url: StandardEndpoint,
            Title: string.IsNullOrWhiteSpace(title) ? "treg" : title.Trim(),
            HeaderName: AuthHeaderName,
            HeaderPrefix: AuthHeaderPrefix,
            AllowedTools: [.. StandardTools],
            ConfirmationMode: confirmationMode);

    /// <summary>
    /// Returns an <see cref="McpConnectionConfig"/> targeting <see cref="RestrictedV2Endpoint"/>
    /// with verified v2 tool names (<c>catalog_search</c>, <c>catalog_get</c>, <c>catalog_call_read</c>,
    /// <c>catalog_call_write</c>, <c>balance</c>) and <see cref="McpConfirmationMode.OncePerSession"/> by default.
    /// </summary>
    public static McpConnectionConfig RestrictedV2(
        McpConfirmationMode confirmationMode = McpConfirmationMode.OncePerSession,
        string? title = null)
        => new(
            Url: RestrictedV2Endpoint,
            Title: string.IsNullOrWhiteSpace(title) ? "treg (v2)" : title.Trim(),
            HeaderName: AuthHeaderName,
            HeaderPrefix: AuthHeaderPrefix,
            AllowedTools: [.. RestrictedV2Tools],
            ConfirmationMode: confirmationMode);

    /// <summary>
    /// Declares the capability-bound <c>treg</c> slot (<c>tool@1</c>) required by imported treg skill apps.
    /// </summary>
    public static AppSlot Slot(bool required = true)
        => new(SlotName, Connector: "", Capability: CapabilityId, Required: required);
}
