using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace DigitalBrain.Platform.Hosting;

internal sealed class HostedWorkspaceStore(IOptions<HostedRuntimeOptions> options, TimeProvider clock)
{
    internal sealed record Grant(string Id, string CodeHash, string UserId, string SessionId, string AccountId, string BrainId, string RuntimeId,
        DateTimeOffset ExchangeUntil, DateTimeOffset ExpiresAt, bool Consumed = false, bool Revoked = false);
    internal sealed record Issued(string Code, DateTimeOffset ExpiresAt);

    public Task<Issued> IssueAsync(string userId, string sessionId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(userId) || userId.Length > 200 || string.IsNullOrWhiteSpace(sessionId) || sessionId.Length > 200)
        { throw new ArgumentException("Workspace subject and session are required."); }
        return UpdateAsync(grants =>
        {
            var now = clock.GetUtcNow();
            grants.RemoveAll(grant => grant.ExpiresAt <= now);
            if (grants.Count >= 1024) { throw new InvalidOperationException("Workspace grant capacity reached."); }
            var code = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var scope = options.Value;
            grants.Add(new(Guid.NewGuid().ToString("N"), Hash(code), userId, sessionId, scope.AccountId, scope.BrainId, scope.RuntimeId,
                now.AddMinutes(1), now.AddMinutes(5)));
            return new Issued(code, now.AddMinutes(1));
        }, ct);
    }

    public Task<Grant?> ExchangeAsync(string code, CancellationToken ct)
    {
        if (code.Length != 64 || !code.All(Uri.IsHexDigit)) { return Task.FromResult<Grant?>(null); }
        return UpdateAsync<Grant?>(grants =>
        {
            var index = grants.FindIndex(grant => grant.CodeHash == Hash(code));
            if (index < 0) { return null; }
            var grant = grants[index];
            if (!MatchesAudience(grant) || grant.Consumed || grant.Revoked || grant.ExchangeUntil <= clock.GetUtcNow()) { return null; }
            grants[index] = grant with { Consumed = true };
            return grants[index];
        }, ct);
    }

    public Task<bool> IsActiveAsync(string id, CancellationToken ct) => UpdateAsync(grants =>
        grants.Any(grant => MatchesAudience(grant) && grant.Id == id && grant.Consumed && !grant.Revoked && grant.ExpiresAt > clock.GetUtcNow()), ct, write: false);

    public Task RevokeSessionAsync(string sessionId, CancellationToken ct) => UpdateAsync(grants =>
    {
        for (var i = 0; i < grants.Count; i++)
        {
            if (grants[i].SessionId == sessionId) { grants[i] = grants[i] with { Revoked = true }; }
        }
        return true;
    }, ct);

    private async Task<T> UpdateAsync<T>(Func<List<Grant>, T> action, CancellationToken ct, bool write = true)
    {
        var directory = options.Value.StateDirectory;
        if (!Path.IsPathFullyQualified(directory)) { throw new InvalidOperationException("Hosted runtime requires a durable absolute state directory."); }
        Directory.CreateDirectory(directory);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        FileStream lease;
        while (true)
        {
            deadline.Token.ThrowIfCancellationRequested();
            try { lease = new FileStream(Path.Combine(directory, "workspace.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); break; }
            catch (IOException) { await Task.Delay(20, deadline.Token); }
        }
        await using var held = lease;
        var path = Path.Combine(directory, "workspace.json");
        var grants = File.Exists(path)
            ? JsonSerializer.Deserialize<List<Grant>>(await File.ReadAllTextAsync(path, deadline.Token)) ?? throw new InvalidDataException("Invalid workspace state.")
            : [];
        var result = action(grants);
        if (write)
        {
            var temporary = path + ".pending";
            await using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(file, grants, cancellationToken: deadline.Token);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        return result;
    }

    private static string Hash(string code) => Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(code)));
    private bool MatchesAudience(Grant grant) => grant.AccountId == options.Value.AccountId &&
        grant.BrainId == options.Value.BrainId && grant.RuntimeId == options.Value.RuntimeId;
}
