using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Contracts.Compute;

namespace IntoChat.OS.Connections.Compute;

public interface IComputeReader
{
    Task<BrainUsageSummary> Read(DateTimeOffset since);
}

public sealed record BrainUsageSummary(long SpentMicros, CostRow[] Rows);
