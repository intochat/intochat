using IntoChat.Cloud.Auth.Core;
using Microsoft.Extensions.Time.Testing;

namespace IntoChat.Cloud.Auth.Core.Tests;

public sealed class CodeEngineTests
{
    private const string Subject = "user@example.com";
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero));
    private readonly SequenceSource _source = new();
    private readonly CodeEngine _engine;

    public CodeEngineTests()
    {
        var hasher = new PurposeHasher("codes", new byte[32]);
        _engine = new CodeEngine(new CodeRules(), hasher, _time, _source);
    }

    [Fact]
    public void Issue_FreshRecord_StoresHashOnlyAndTenMinuteExpiry()
    {
        var result = _engine.Issue(null, CodePurpose.Signup, Subject, isDecoy: false, enforceCooldown: true);

        Assert.Equal(IssueKind.Issued, result.Kind);
        Assert.Equal(6, result.PlainCode!.Length);
        Assert.All(result.PlainCode, c => Assert.True(char.IsDigit(c)));
        Assert.StartsWith("ph1:codes:", result.Record.CodeHash, StringComparison.Ordinal);
        Assert.DoesNotContain(result.PlainCode, result.Record.CodeHash, StringComparison.Ordinal);
        Assert.Equal(_time.GetUtcNow() + TimeSpan.FromMinutes(10), result.Record.ExpiresAt);
        Assert.Single(result.Record.SendTimes);
        Assert.Equal(_time.GetUtcNow() + TimeSpan.FromSeconds(42), result.Issued!.ResendAvailableAt);
        Assert.Equal(3, result.Record.AttemptsLeft);
    }

    [Fact]
    public void IssueForSubject_PrecomputedHash_KeepsHashAndEncodesItsPrefixInTheId()
    {
        const string subjectHash = "0123abcd";
        var result = _engine.IssueForSubject(null, CodePurpose.Login, subjectHash, isDecoy: true, enforceCooldown: false);

        Assert.Equal(IssueKind.Issued, result.Kind);
        Assert.Equal(subjectHash, result.Record.SubjectHash);
        Assert.True(CodeId.TryParse(result.Record.Id, out var purpose, out var subject128, out _));
        Assert.Equal(CodePurpose.Login, purpose);
        Assert.Equal(CodeEngine.SubjectKeyOf(subjectHash), Convert.ToHexStringLower(subject128));
    }

    [Fact]
    public void Issue_WithinCooldown_IsRateLimitedWithRetryAt()
    {
        var first = _engine.Issue(null, CodePurpose.Signup, Subject, false, true);
        _time.Advance(TimeSpan.FromSeconds(41));

        var second = _engine.Issue(first.Record, CodePurpose.Signup, Subject, false, true);

        Assert.Equal(IssueKind.RateLimited, second.Kind);
        Assert.Equal(first.Record.SentAt + TimeSpan.FromSeconds(42), second.RetryAt);
        Assert.Null(second.PlainCode);
    }

    [Fact]
    public void Issue_AfterCooldown_Succeeds()
    {
        var first = _engine.Issue(null, CodePurpose.Signup, Subject, false, true);
        _time.Advance(TimeSpan.FromSeconds(42));

        var second = _engine.Issue(first.Record, CodePurpose.Signup, Subject, false, true);

        Assert.Equal(IssueKind.Issued, second.Kind);
        Assert.NotEqual(first.Record.Id, second.Record.Id);
    }

    [Fact]
    public void Issue_SixthSendInsideAnHour_IsRateLimited()
    {
        var record = Send(null);
        for (var i = 0; i < 4; i++)
        {
            _time.Advance(TimeSpan.FromMinutes(1));
            record = Send(record);
        }

        _time.Advance(TimeSpan.FromMinutes(1));
        var sixth = _engine.Issue(record, CodePurpose.Signup, Subject, false, true);

        Assert.Equal(IssueKind.RateLimited, sixth.Kind);
        Assert.Equal(record.SendTimes[0] + TimeSpan.FromHours(1), sixth.RetryAt);
    }

    [Fact]
    public void Verify_WrongCode_ReportsAttemptsLeft2Then1()
    {
        var issued = _engine.Issue(null, CodePurpose.Signup, Subject, false, true);
        var first = _engine.Verify(issued.Record, issued.Record.Id, Wrong(issued.PlainCode!));
        var second = _engine.Verify(first.Record, issued.Record.Id, Wrong(issued.PlainCode!));

        Assert.Equal(VerifyKind.Invalid, first.Kind);
        Assert.Equal(2, first.AttemptsLeft);
        Assert.Equal(1, second.AttemptsLeft);
        Assert.Equal(AuthErrors.EmailCodeInvalid, second.Error);
    }

    [Fact]
    public void Verify_ThirdWrongCode_LocksAndReissuesWithNewNonce()
    {
        var issued = _engine.Issue(null, CodePurpose.Signup, Subject, false, true);
        var record = issued.Record;
        VerifyResult result = null!;
        for (var i = 0; i < 3; i++)
        {
            result = _engine.Verify(record, issued.Record.Id, Wrong(issued.PlainCode!));
            record = result.Record;
        }

        Assert.Equal(VerifyKind.Locked, result.Kind);
        Assert.Equal(AuthErrors.EmailCodeLocked, result.Error);
        Assert.NotEqual(issued.Record.Id, result.Reissued!.CodeId);
        Assert.NotNull(result.PlainCode);
        Assert.Equal(VerifyKind.NotFound, _engine.Verify(result.Record, issued.Record.Id, issued.PlainCode!).Kind);
    }

    [Fact]
    public void Verify_Expired_ReissuesWithoutCooldown()
    {
        var issued = _engine.Issue(null, CodePurpose.Signup, Subject, false, true);
        _time.Advance(TimeSpan.FromMinutes(10));

        var result = _engine.Verify(issued.Record, issued.Record.Id, issued.PlainCode!);

        Assert.Equal(VerifyKind.Expired, result.Kind);
        Assert.NotEqual(issued.Record.Id, result.Reissued!.CodeId);
        Assert.Equal(2, result.Record.SendTimes.Count);
    }

    [Fact]
    public void Verify_ExpiredAtHourCap_IsRateLimitedCarryingExpiredReason()
    {
        var record = Send(null);
        for (var i = 0; i < 4; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(50));
            record = Send(record);
        }

        _time.Advance(TimeSpan.FromMinutes(10));
        var result = _engine.Verify(record, record.Id, "000000");

        Assert.Equal(VerifyKind.RateLimited, result.Kind);
        Assert.Equal("expired", result.Reason);
        Assert.NotNull(result.RetryAt);
        Assert.False(result.Record.Active);
    }

    [Fact]
    public void Verify_LockedAtHourCap_IsRateLimitedCarryingLockedReason()
    {
        var record = Send(null);
        for (var i = 0; i < 4; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(50));
            record = Send(record);
        }

        var plain = _source.LastCode;
        VerifyResult result = null!;
        for (var i = 0; i < 3; i++)
        {
            result = _engine.Verify(record, record.Id, Wrong(plain));
            record = result.Record;
        }

        Assert.Equal(VerifyKind.RateLimited, result.Kind);
        Assert.Equal("locked", result.Reason);
    }

    [Fact]
    public void Verify_CorrectCode_SucceedsOnceThenNotFound()
    {
        var issued = _engine.Issue(null, CodePurpose.Signup, Subject, false, true);

        var ok = _engine.Verify(issued.Record, issued.Record.Id, issued.PlainCode!);
        var again = _engine.Verify(ok.Record, issued.Record.Id, issued.PlainCode!);

        Assert.Equal(VerifyKind.Success, ok.Kind);
        Assert.Null(ok.Error);
        Assert.True(ok.Record.Consumed);
        Assert.Equal(VerifyKind.NotFound, again.Kind);
    }

    [Fact]
    public void Verify_StaleNonce_IsNotFound()
    {
        var issued = _engine.Issue(null, CodePurpose.Signup, Subject, false, true);
        _time.Advance(TimeSpan.FromSeconds(42));
        var reissued = _engine.Issue(issued.Record, CodePurpose.Signup, Subject, false, true);

        var stale = _engine.Verify(reissued.Record, issued.Record.Id, issued.PlainCode!);

        Assert.Equal(VerifyKind.NotFound, stale.Kind);
    }

    [Fact]
    public void Verify_DecoyRecordWithRightDigits_NeverSucceeds()
    {
        var issued = _engine.Issue(null, CodePurpose.Signup, Subject, isDecoy: true, enforceCooldown: true);

        var result = _engine.Verify(issued.Record, issued.Record.Id, issued.PlainCode!);

        Assert.Equal(VerifyKind.Invalid, result.Kind);
        Assert.False(result.Record.Consumed);
    }

    [Fact]
    public void Decoy_RunsIdenticalRules_SameResponseShapeAsReal()
    {
        var real = _engine.Issue(null, CodePurpose.Signup, Subject, false, true);
        var decoy = _engine.Issue(null, CodePurpose.Signup, "other@example.com", true, true);

        Assert.Equal(real.Issued!.ExpiresAt, decoy.Issued!.ExpiresAt);
        Assert.Equal(real.Issued.ResendAvailableAt, decoy.Issued.ResendAvailableAt);
        Assert.Equal(real.Issued.CodeId.Length, decoy.Issued.CodeId.Length);
    }

    [Fact]
    public void RedeemTicket_ValidOnceWithinTenMinutes_ThenInvalid()
    {
        var (record, ticket) = ConsumedResetTicket();

        var first = _engine.RedeemTicket(record, ticket);
        var second = _engine.RedeemTicket(first.Record, ticket);

        Assert.Equal(TicketResult.Redeemed, first.Result);
        Assert.Equal(TicketResult.TicketInvalid, second.Result);
    }

    [Fact]
    public void RedeemTicket_Expired_IsInvalid()
    {
        var (record, ticket) = ConsumedResetTicket();
        _time.Advance(TimeSpan.FromMinutes(10));

        Assert.Equal(TicketResult.TicketInvalid, _engine.RedeemTicket(record, ticket).Result);
    }

    [Fact]
    public void RedeemTicket_WrongTicket_IsInvalid()
    {
        var (record, _) = ConsumedResetTicket();

        Assert.Equal(TicketResult.TicketInvalid, _engine.RedeemTicket(record, "not-the-ticket").Result);
    }

    [Fact]
    public void IssueTicket_OnUnconsumedRecord_Throws()
    {
        var issued = _engine.Issue(null, CodePurpose.Reset, Subject, false, true);

        Assert.Throws<InvalidOperationException>(() => _engine.IssueTicket(issued.Record));
    }

    [Fact]
    public void CodeRecord_ToString_OmitsHashes()
    {
        var issued = _engine.Issue(null, CodePurpose.Signup, Subject, false, true);

        var text = issued.Record.ToString();

        Assert.DoesNotContain("ph1:", text, StringComparison.Ordinal);
        Assert.DoesNotContain(issued.Record.Id, text, StringComparison.Ordinal);
    }

    private (CodeRecord Record, string Ticket) ConsumedResetTicket()
    {
        var issued = _engine.Issue(null, CodePurpose.Reset, Subject, false, true);
        var ok = _engine.Verify(issued.Record, issued.Record.Id, issued.PlainCode!);
        return _engine.IssueTicket(ok.Record);
    }

    private CodeRecord Send(CodeRecord? existing)
    {
        var result = _engine.Issue(existing, CodePurpose.Signup, Subject, false, enforceCooldown: true);
        Assert.Equal(IssueKind.Issued, result.Kind);
        return result.Record;
    }

    private static string Wrong(string code) => code == "000000" ? "000001" : "000000";

    private sealed class SequenceSource : ICodeSource
    {
        private int _n;

        public string LastCode { get; private set; } = "";

        public string NewCode()
        {
            _n++;
            LastCode = (100000 + (_n * 7919 % 800000)).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
            return LastCode;
        }

        public byte[] NewNonce() => BitConverter.GetBytes((long)_n + 1);

        public string NewTicket() => "ticket-" + _n;
    }
}