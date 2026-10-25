namespace IntoChat.OS.Framework.Compute;

public interface IComputeReader
{
    Task<BrainUsageSummary> Read(DateTimeOffset since);
}

public sealed record BrainUsageSummary(long SpentMicros, CostRow[] Rows);
