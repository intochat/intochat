using Orleans;
using Orleans.Concurrency;

namespace IntoChat.OS.Modules.Apps;

/// <summary>
/// Stateless worker grain wrapping CPU-intensive relational analytics pipelines.
/// Pools across CPU cores without blocking stateful grain turns.
/// </summary>
[StatelessWorker]
internal sealed class DataAnalyticsWorker : Grain, IDataAnalyticsWorker
{
    public Task<DataQueryResult> ExecuteAsync(
        string queryOrPrompt,
        string database,
        string? fileDatasetText = null,
        string? fileDatasetPath = null)
    {
        var result = DataAnalyticsEngine.ExecuteInMemory(queryOrPrompt, database, fileDatasetText, fileDatasetPath);
        return Task.FromResult(result);
    }
}
