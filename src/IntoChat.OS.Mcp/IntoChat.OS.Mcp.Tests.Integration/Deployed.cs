namespace IntoChat.OS.Mcp.Tests.Integration;

// The contract assemblies this MCP host carries, as a kernel's compiled layer.
internal static class Deployed
{
    public static FixedKernel Kernel() => new(typeof(INeuron).Assembly, typeof(IntoChat.OS.Integrations.IConnectionRequests).Assembly,
        typeof(IntoChat.OS.Modules.Apps.IApp).Assembly, typeof(IntoChat.OS.Modules.Mcp.IMcpServer).Assembly, typeof(IntoChat.OS.Modules.Postgres.IPostgresServer).Assembly,
        typeof(IntoChat.OS.Modules.Time.Timers.ITimer).Assembly, typeof(IntoChat.OS.Modules.Circles.ICircle).Assembly);
}
