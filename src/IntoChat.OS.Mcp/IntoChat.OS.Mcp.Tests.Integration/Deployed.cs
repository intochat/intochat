namespace IntoChat.OS.Mcp.Tests.Integration;

// The contract assemblies this MCP host carries, as a kernel's compiled layer.
internal static class Deployed
{
    public static FixedKernel Kernel() => new(typeof(INeuron).Assembly, typeof(IntoChat.OS.Integrations.IConnectionRequests).Assembly,
        typeof(IntoChat.OS.Apps.IApp).Assembly, typeof(IntoChat.OS.Mcp.IMcpServer).Assembly, typeof(IntoChat.OS.Postgres.IPostgresServer).Assembly,
        typeof(IntoChat.OS.Time.Timers.ITimer).Assembly, typeof(IntoChat.OS.Circles.ICircle).Assembly);
}
