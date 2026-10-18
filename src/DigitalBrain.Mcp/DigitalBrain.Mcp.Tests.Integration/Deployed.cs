namespace DigitalBrain.Mcp.Tests.Integration;

// The contract assemblies this MCP host carries, as a kernel's compiled layer.
internal static class Deployed
{
    public static FixedKernel Kernel() => new(typeof(INeuron).Assembly, typeof(DigitalBrain.Integrations.IConnectionRequests).Assembly,
        typeof(DigitalBrain.Apps.IApp).Assembly, typeof(DigitalBrain.Mcp.IMcpServer).Assembly, typeof(DigitalBrain.Postgres.IPostgresServer).Assembly,
        typeof(DigitalBrain.Time.Timers.ITimer).Assembly, typeof(DigitalBrain.Circles.ICircle).Assembly);
}
