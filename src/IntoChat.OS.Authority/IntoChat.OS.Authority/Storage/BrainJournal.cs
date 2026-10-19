namespace IntoChat.OS.Authority.Storage;

internal sealed class BrainJournal(BrainLogs logs) : IBrainJournal
{
    public async Task<BrainRecord?> ReadAsync(string scope, string key, string state, CancellationToken ct)
        => await (await logs.For(scope).WaitAsync(ct)).Read(key, state);

    public async Task<long> AppendAsync(string scope, string key, string state, long? etag, BrainRecordChange change, byte[] bytes, CancellationToken ct)
        => await (await logs.For(scope).WaitAsync(ct)).Append(key, state, etag, change, bytes, [], ct);

    public Task<bool> CheckAsync(string scope, CancellationToken ct) => logs.CheckStorageAsync(scope, ct);
}
