using IntoChat.Cloud.Auth.Core;
using IntoChat.Cloud.Auth.Services;
using Microsoft.Extensions.Logging;

namespace IntoChat.Cloud.Auth.Neurons;

[DigitalBrain.Neurons.Neuron("auth.code")]
internal sealed class AuthCodeNeuron(
    AuthHashing hashing,
    CodeMailer mailer,
    TimeProvider clock,
    ILogger<AuthCodeNeuron> logger) : AuthNeuron<AuthCodeState>, IAuthCode
{

    private CodeEngine? engine;

    private CodeEngine Engine => engine ??= new CodeEngine(new CodeRules(), hashing.For(AuthHashing.Codes), clock);

    private CodePurpose Purpose => Enum.Parse<CodePurpose>(FullKey[(FullKey.LastIndexOf('/') + 1)..]);

    public async Task<CodeIssue> Issue(CodeIssueRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (AuthCodeKeys.ForSubject(request.SubjectHash, Purpose) != FullKey)
        {
            throw new ArgumentException("The subject does not belong to this code slot.", nameof(request));
        }

        var result = Engine.IssueForSubject(Current(), Purpose, request.SubjectHash, request.IsDecoy, request.EnforceCooldown);
        if (result.Kind == IssueKind.RateLimited)
        {
            return new CodeIssue(CodeIssueOutcome.RateLimited, null, null, null, result.RetryAt);
        }

        await SaveQuietly(AuthCodeState.From(result.Record, request.Delivery, request.AllowedMethods, request.AmrOrigin));
        Deliver(request.Delivery, request.IsDecoy ? null : result.PlainCode, notice: request.IsDecoy);
        return Issued(result.Issued!);
    }

    public async Task<CodeIssue> Resend()
    {
        if (!Exists || string.IsNullOrEmpty(Snapshot.Id))
        {
            return new CodeIssue(CodeIssueOutcome.RateLimited, null, null, null, clock.GetUtcNow());
        }

        var state = Snapshot;
        var result = Engine.IssueForSubject(state.ToRecord(), Purpose, state.SubjectHash, state.IsDecoy, enforceCooldown: true);
        if (result.Kind == IssueKind.RateLimited)
        {
            return new CodeIssue(CodeIssueOutcome.RateLimited, null, null, null, result.RetryAt);
        }

        await SaveQuietly(AuthCodeState.From(result.Record, state.Delivery, state.AllowedMethods, state.AmrOrigin));
        Deliver(state.Delivery, state.IsDecoy ? null : result.PlainCode, notice: false);
        return Issued(result.Issued!);
    }

    public async Task<CodeVerdict> Verify(string codeId, string code)
    {
        if (Current() is not { } record || record.Id != codeId)
        {
            return new CodeVerdict(CodeOutcome.NotFound, null, null, null, null);
        }

        var result = Engine.Verify(record, codeId, code ?? "");
        var state = Snapshot;
        await SaveQuietly(AuthCodeState.From(result.Record, state.Delivery, state.AllowedMethods, state.AmrOrigin));
        if (result.Reissued is not null)
        {
            Deliver(state.Delivery, state.IsDecoy ? null : result.PlainCode, notice: false);
        }

        return result.Kind switch
        {
            VerifyKind.Success => new CodeVerdict(CodeOutcome.Success, null, null, null, state.SubjectHash),
            VerifyKind.Invalid => new CodeVerdict(CodeOutcome.Invalid, result.AttemptsLeft, null, null, null),
            VerifyKind.Expired => new CodeVerdict(CodeOutcome.Expired, null, Issued(result.Reissued!), null, null),
            VerifyKind.Locked => new CodeVerdict(CodeOutcome.Locked, null, Issued(result.Reissued!), null, null),
            VerifyKind.RateLimited => new CodeVerdict(CodeOutcome.RateLimited, null, null, result.RetryAt, null),
            _ => new CodeVerdict(CodeOutcome.NotFound, null, null, null, null),
        };
    }

    // An authenticator or recovery attempt spends the same budget as a wrong email code (12-07).
    public async Task<AttemptReservation> ReserveAttempt(string codeId)
    {
        if (Current() is not { } record || record.Id != codeId || !record.Active || clock.GetUtcNow() >= record.ExpiresAt)
        {
            return new AttemptReservation(false, 0, null);
        }

        if (record.AttemptsLeft <= 0)
        {
            return new AttemptReservation(false, 0, null);
        }

        var state = Snapshot;
        var spent = record with { AttemptsLeft = record.AttemptsLeft - 1 };
        await SaveQuietly(AuthCodeState.From(spent, state.Delivery, state.AllowedMethods, state.AmrOrigin));
        return new AttemptReservation(true, spent.AttemptsLeft, null);
    }

    public async Task<bool> Consume(string codeId)
    {
        if (Current() is not { } record || record.Id != codeId || !record.Active || clock.GetUtcNow() >= record.ExpiresAt)
        {
            return false;
        }

        var state = Snapshot;
        await SaveQuietly(AuthCodeState.From(record with { Active = false, Consumed = true }, state.Delivery, state.AllowedMethods, state.AmrOrigin));
        return true;
    }

    public Task<ChallengeInfo?> GetChallenge(string codeId) =>
        Task.FromResult(Current() is { } record && record.Id == codeId && record.Active && clock.GetUtcNow() < record.ExpiresAt
            ? new ChallengeInfo(Snapshot.AllowedMethods, Snapshot.AmrOrigin, record.SubjectHash)
            : null);

    // A durable proof lets an interrupted email-index move resume after its code was consumed. No code leaves
    // the neuron, and the HTTP caller must still own the destination reservation before completing the move.
    public Task<string?> GetVerifiedChangeSubject(string codeId) => Task.FromResult(
        Current() is { Purpose: CodePurpose.ChangeEmail, Consumed: true } record && record.Id == codeId
            ? record.SubjectHash : null);

    public Task<string?> GetChangeSubject() => Task.FromResult(
        Current() is { Purpose: CodePurpose.ChangeEmail } record ? record.SubjectHash : null);

    public async Task<TicketIssue?> IssueTicket(string codeId)
    {
        if (Current() is not { } record || record.Id != codeId || record.Purpose != CodePurpose.Reset || !record.Consumed || record.TicketHash is not null)
        {
            return null;
        }

        var (withTicket, ticket) = Engine.IssueTicket(record);
        var state = Snapshot;
        await SaveQuietly(AuthCodeState.From(withTicket, state.Delivery, state.AllowedMethods, state.AmrOrigin));
        return new TicketIssue(ticket, withTicket.TicketExpiresAt!.Value);
    }

    public async Task<bool> RedeemTicket(string ticket)
    {
        if (Current() is not { } record)
        {
            return false;
        }

        var (redeemed, result) = Engine.RedeemTicket(record, ticket);
        if (result != TicketResult.Redeemed)
        {
            return false;
        }

        var state = Snapshot;
        await SaveQuietly(AuthCodeState.From(redeemed, state.Delivery, state.AllowedMethods, state.AmrOrigin));
        return true;
    }

    private CodeRecord? Current() => Exists && !string.IsNullOrEmpty(Snapshot.Id) ? Snapshot.ToRecord() : null;

    private static CodeIssue Issued(CodeIssued issued) =>
        new(CodeIssueOutcome.Issued, issued.CodeId, issued.ResendAvailableAt, issued.ExpiresAt, null);

    // A one-shot timer after the state is saved: the plain code lives only in this closure on its way to the mailer.
    // Real and decoy issues schedule alike; a decoy sends at most its notice (AccountAlreadyExists).
    private void Deliver(CodeDelivery? delivery, string? code, bool notice)
    {
        var send = delivery is null ? null
            : code is not null ? (Func<Task>)(() => mailer.SendAsync(delivery, code))
            : notice && delivery.NoticeTemplate is not null ? () => mailer.SendAsync(delivery, null)
            : null;
        if (send is null) { return; }
        // Off the request path and outside this turn: the code was saved first and lives only in this closure. The execution context
        // does not flow, so the delivery carries neither the requesting person's stamp nor this turn's request context (an internal
        // continuation is not an operation in the requester's selected brain). The mailer absorbs every mail outcome.
        using (ExecutionContext.SuppressFlow())
        {
            _ = Task.Run(async () =>
            {
                try { await send(); }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    logger.LogWarning("Auth code delivery failed: {Error}", error.GetType().Name);
                }
            });
        }
    }
}
