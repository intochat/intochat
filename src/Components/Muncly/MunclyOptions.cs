using DigitalBrain.Modules.Options;

namespace IntoChat.Muncly;

public sealed class MunclyOptions : IModuleOptions
{
    public const string DefaultMcpServerName = "muncly-crm";

    [Setting]
    public string? BaseUrl { get; set; }

    [Setting]
    public string? ToolSets { get; set; }

    [Setting]
    public string[]? AllowedTools { get; set; }

    [Setting]
    public bool Enabled { get; set; } = true;

    public string? BuildMcpEndpointUrl()
    {
        if (string.IsNullOrWhiteSpace(BaseUrl) || !Uri.TryCreate(BaseUrl.Trim(), UriKind.Absolute, out var uri))
        {
            return null;
        }

        var root = uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        var mcp = $"{root}/api/mcp";
        if (!string.IsNullOrWhiteSpace(ToolSets))
        {
            mcp += $"?sets={Uri.EscapeDataString(ToolSets.Trim())}";
        }

        return mcp;
    }

    public void Validate()
    {
        if (!string.IsNullOrWhiteSpace(BaseUrl))
        {
            if (!Uri.TryCreate(BaseUrl.Trim(), UriKind.Absolute, out var uri)
                || !string.IsNullOrEmpty(uri.UserInfo)
                || string.IsNullOrWhiteSpace(uri.Host))
            {
                throw new ArgumentException("Muncly BaseUrl must be an absolute https URL (http is allowed only for loopback) without credentials.");
            }

            var allowed = uri.Scheme == Uri.UriSchemeHttps
                || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback);
            if (!allowed)
            {
                throw new ArgumentException("Muncly BaseUrl must use https (http is allowed only for loopback).");
            }
        }

        if (AllowedTools is not null && AllowedTools.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Muncly AllowedTools entries must be non-empty.");
        }
    }
}
