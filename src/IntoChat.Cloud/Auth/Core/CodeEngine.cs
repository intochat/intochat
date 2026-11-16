using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace IntoChat.Cloud.Auth.Core;

/// <summary>Injectable randomness so tests are deterministic.</summary>
public interface ICodeSource
{
    string NewCode();

    byte[] NewNonce();

    string NewTicket();
}

public sealed class RandomCodeSource : ICodeSource
{
    public static readonly RandomCodeSource Shared = new();

    public string NewCode() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", CultureInfo.InvariantCulture);

    public byte[] NewNonce() => RandomNumberGenerator.GetBytes(CodeId.NonceLength);

    public string NewTicket() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>State of one subject+purpose slot. Holds hashes only, never plain codes.</summary>
public sealed record CodeRecord(
    CodePurpose Purpose,
    string SubjectHash,
    string Id,
    string CodeHash,
    DateTimeOffset ExpiresAt,
    DateTimeOffset SentAt,
    int AttemptsLeft,
    bool Active,
    bool IsDecoy,
    bool Consumed,
    IReadOnlyList<DateTimeOffset> SendTimes,
    string? TicketHash = null,
    DateTimeOffset? TicketExpiresAt = null,
    bool TicketUsed = false)
{
    public override string ToString() =>
        $"CodeRecord {{ Purpose = {Purpose}, Active = {Active}, Consumed = {Consumed}, AttemptsLeft = {AttemptsLeft}, IsDecoy = {IsDecoy} }}";
}

/// <summary>Pure W4 D1 state machine: Issue / Verify / ticket, over immutable <see cref="CodeRecord"/>.</summary>
public sealed class CodeEngine(CodeRules rules, PurposeHasher codesHasher, TimeProvider timeProvider, ICodeSource? source = null)
{
    private readonly ICodeSource _source = source ?? RandomCodeSource.Shared;

    public CodeRules Rules { get; } = rules;

    public IssueResult Issue(CodeRecord? existing, CodePurpose purpose, string normalizedSubject, bool isDecoy, bool enforceCooldown) =>
        IssueCore(existing, purpose, codesHasher.Hash("email:" + normalizedSubject), isDecoy, enforceCooldown);

    /// <summary>Issue for a subject already hashed by the caller (the neuron key carries the hash, never the address).</summary>
    public IssueResult IssueForSubject(CodeRecord? existing, CodePurpose purpose, string subjectHash, bool isDecoy, bool enforceCooldown)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectHash);
        return IssueCore(existing, purpose, subjectHash, isDecoy, enforceCooldown);
    }

    /// <summary>The lowercase hex of the 128-bit subject prefix a code id carries: routes a code id to its slot.</summary>
    public static string SubjectKeyOf(string subjectHash) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(subjectHash))[..CodeId.SubjectHashLength]);

    public VerifyResult Verify(CodeRecord record, string codeId, string code)
    {
        var now = timeProvider.GetUtcNow();
        if (!record.Active || record.Id != codeId)
        {
            return new VerifyResult(record, VerifyKind.NotFound);
        }

        if (now >= record.ExpiresAt)
        {
            return Reissue(record with { Active = false }, VerifyKind.Expired, "expired");
        }

        if (record.AttemptsLeft <= 0)
        {
            return new VerifyResult(record, VerifyKind.NotFound);
        }

        record = record with { AttemptsLeft = record.AttemptsLeft - 1 };
        if (!record.IsDecoy && codesHasher.Verify(HashInput(record.Id, code), record.CodeHash))
        {
            return new VerifyResult(record with { Active = false, Consumed = true }, VerifyKind.Success);
        }

        if (record.AttemptsLeft > 0)
        {
            return new VerifyResult(record, VerifyKind.Invalid, AttemptsLeft: record.AttemptsLeft);
        }

        return Reissue(record with { Active = false }, VerifyKind.Locked, "locked");
    }

    /// <summary>Attach a single-use ticket to a consumed Reset record. Returns the plain ticket once.</summary>
    public (CodeRecord Record, string Ticket) IssueTicket(CodeRecord consumed)
    {
        if (consumed.Purpose != CodePurpose.Reset || !consumed.Consumed)
        {
            throw new InvalidOperationException("A reset ticket needs a consumed reset code.");
        }

        var ticket = _source.NewTicket();
        var now = timeProvider.GetUtcNow();
        return (consumed with { TicketHash = codesHasher.Hash(TicketInput(ticket)), TicketExpiresAt = now + Rules.ResetTicketLifetime, TicketUsed = false }, ticket);
    }

    public (CodeRecord Record, TicketResult Result) RedeemTicket(CodeRecord record, string? ticket)
    {
        var now = timeProvider.GetUtcNow();
        if (string.IsNullOrWhiteSpace(ticket) || record.TicketHash is null || record.TicketUsed
            || record.TicketExpiresAt is null || now >= record.TicketExpiresAt
            || !codesHasher.Verify(TicketInput(ticket), record.TicketHash))
        {
            return (record, TicketResult.TicketInvalid);
        }

        return (record with { TicketUsed = true }, TicketResult.Redeemed);
    }

    private IssueResult IssueCore(CodeRecord? existing, CodePurpose purpose, string subjectHash, bool isDecoy, bool enforceCooldown)
    {
        var now = timeProvider.GetUtcNow();
        var recent = (existing?.SendTimes ?? []).Where(t => t > now - Rules.SendCapWindow).Order().ToArray();
        if (enforceCooldown && recent.Length > 0 && recent[^1] + Rules.ResendCooldown > now)
        {
            return RateLimited(existing, purpose, subjectHash, isDecoy, recent[^1] + Rules.ResendCooldown);
        }

        if (recent.Length >= Rules.HourlySendCap)
        {
            return RateLimited(existing, purpose, subjectHash, isDecoy, recent[^Rules.HourlySendCap] + Rules.SendCapWindow);
        }

        var subject128 = SHA256.HashData(Encoding.UTF8.GetBytes(subjectHash))[..CodeId.SubjectHashLength];
        var id = CodeId.Encode(purpose, subject128, _source.NewNonce());
        var code = _source.NewCode();
        var record = new CodeRecord(
            purpose,
            subjectHash,
            id,
            codesHasher.Hash(HashInput(id, code)),
            now + Rules.Lifetime,
            now,
            Rules.MaxAttempts,
            Active: true,
            isDecoy,
            Consumed: false,
            [.. recent, now]);
        return new IssueResult(record, IssueKind.Issued, code, new CodeIssued(id, now + Rules.ResendCooldown, record.ExpiresAt), null);
    }

    private static IssueResult RateLimited(CodeRecord? existing, CodePurpose purpose, string subjectHash, bool isDecoy, DateTimeOffset retryAt)
    {
        var record = existing ?? new CodeRecord(purpose, subjectHash, string.Empty, string.Empty, DateTimeOffset.MinValue, DateTimeOffset.MinValue, 0, false, isDecoy, false, []);
        return new IssueResult(record, IssueKind.RateLimited, null, null, retryAt);
    }

    private VerifyResult Reissue(CodeRecord invalidated, VerifyKind outcome, string reason)
    {
        var issue = IssueCore(invalidated, invalidated.Purpose, invalidated.SubjectHash, invalidated.IsDecoy, enforceCooldown: false);
        return issue.Kind == IssueKind.RateLimited
            ? new VerifyResult(invalidated, VerifyKind.RateLimited, RetryAt: issue.RetryAt, Reason: reason)
            : new VerifyResult(issue.Record, outcome, Reissued: issue.Issued, PlainCode: issue.PlainCode);
    }

    private static string HashInput(string codeId, string code) => $"code:{codeId}:{code.Trim()}";

    private static string TicketInput(string ticket) => "reset-ticket:" + ticket.Trim();
}
