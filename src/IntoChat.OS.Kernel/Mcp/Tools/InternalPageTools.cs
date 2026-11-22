using System.ComponentModel;
using IntoChat.OS.Kernel.Auth;
using IntoChat.OS.Modules.Identity.Accounts;
using ModelContextProtocol.Server;

namespace IntoChat.OS.Kernel.Mcp.Tools;

[McpServerToolType]
internal sealed class InternalPageTools(InternalPageInspector inspector, McpCaller caller)
{
    private const string InspectName = "inspect_os_page";

    [McpServerTool(Name = InspectName), Description("Inspect an internal IntoChat OS page (e.g. '/orleans/', '/devui/', '/widgetbook/') in a headless browser using an authenticated one-time launch ticket, returning HTTP status, title, and console errors.")]
    public Task<InternalPageReport> InspectOsPageAsync(
        [Description("Safe relative OS path starting with '/', such as '/orleans/', '/devui/', or '/widgetbook/'.")] string relativePath,
        CancellationToken cancellationToken = default)
    {
        var stamped = caller.Stamp(InspectName);
        var principal = AccountSession.SessionUser(stamped);
        return inspector.InspectAsync(principal, relativePath, cancellationToken);
    }
}
