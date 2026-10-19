namespace IntoChat.OS.Authority.Storage;

// Host-level access to one brain scope's journal. Sealed keys and the raw log stay inside Authority.
public interface IBrainJournal
{
    Task<BrainRecord?> ReadAsync(string scope, string key, string state, CancellationToken ct);
    Task<long> AppendAsync(string scope, string key, string state, long? etag, BrainRecordChange change, byte[] bytes, CancellationToken ct);
    Task<bool> CheckAsync(string scope, CancellationToken ct);
}
