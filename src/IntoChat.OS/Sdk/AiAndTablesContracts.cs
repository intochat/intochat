namespace IntoChat.Sdk;

/// <summary>AI completion and structured extraction gateway exposed to sandboxed apps.</summary>
public interface IAi
{
    Task<string> AskAsync(string prompt, string? systemPrompt = null, CancellationToken cancellationToken = default);
    Task<T> ExtractAsync<T>(string prompt, CancellationToken cancellationToken = default);
}

/// <summary>Column schema descriptor for an app-owned table.</summary>
[GenerateSerializer, Alias("sdk.table-column")]
public sealed record TableColumnSpec(
    [property: Id(0)] string Name,
    [property: Id(1)] string Type,
    [property: Id(2)] bool Required = true);

/// <summary>Keyed JSON document row stored in an app-owned table.</summary>
[GenerateSerializer, Alias("sdk.table-row")]
public sealed record TableRow(
    [property: Id(0)] string Key,
    [property: Id(1)] string Json);

/// <summary>Handle to one app-scoped durable table.</summary>
public interface ITableHandle
{
    Task EnsureAsync(TableColumnSpec[] columns, CancellationToken cancellationToken = default);
    Task UpsertAsync(string key, string jsonRow, CancellationToken cancellationToken = default);
    Task<string?> GetAsync(string key, CancellationToken cancellationToken = default);
    Task<TableRow[]> ListAsync(int offset = 0, int limit = 100, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default);
}

/// <summary>App-scoped structured persistence capability.</summary>
public interface ITables
{
    ITableHandle Table(string name);
}
