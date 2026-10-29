using DigitalBrain.Capabilities;
using IntoChat.Cloud.Auth.Core;
using IntoChat.Cloud.Auth.Neurons;
using IntoChat.Cloud.Auth.Ui;
using IntoChat.Cloud.Mail.Templates;

namespace IntoChat.Cloud.Auth.Services;

// The account value tree's email and password writes. Registered by the cloud kernel before the module's TryAdd of the
// unconfigured double, so a cloud host confirms codes and rotates credentials through the same neurons as the HTTP API.
internal sealed class AccountAuth(IAuthHostSeam host, AuthCalls calls, AuthHashing hashing, AuthModuleOptions options) : IAccountAuth
{
    public async Task RequestEmailChange(string email, CancellationToken cancellationToken = default)
    {
        if (host.CurrentCaller is not { PrincipalId.Length: > 0, AccountId.Length: > 0, BrainId.Length: > 0 } caller
            || EmailNormalizer.Normalize(email) is not { } normalized)
        {
            return;
        }

        var hash = hashing.EmailSubject(normalized);
        var previous = await calls.Credential(caller.PrincipalId, credential => credential.GetEmailHash());
        if (previous is null) { return; }
        var reserved = await calls.EmailIndex(hash, index => index.ReserveForChange(caller.PrincipalId, caller.AccountId, caller.BrainId, normalized, previous));
        await calls.Code(hash, CodePurpose.ChangeEmail, code => code.Issue(new CodeIssueRequest(
            hash, !reserved, true,
            reserved ? new CodeDelivery(normalized, "en", normalized, MailTemplateId.ChangeEmailCode, null) : null,
            ["email"], "pwd")));
    }

    public async Task<bool> ConfirmEmail(string email, string code, CancellationToken cancellationToken = default)
    {
        if (host.CurrentCaller is not { PrincipalId.Length: > 0 } caller
            || EmailNormalizer.Normalize(email) is not { } normalized
            || string.IsNullOrWhiteSpace(code))
        {
            return false;
        }

        var hash = hashing.EmailSubject(normalized);
        var entry = await calls.EmailIndex(hash, index => index.Get());
        if (entry.PrincipalId != caller.PrincipalId || entry.PreviousEmailHash is null || entry.ChangeCodeId is not { Length: > 0 } codeId)
        {
            return false;
        }

        if (entry.Status == EmailIndexStatus.ChangePending && !entry.ChangeProved)
        {
            var verdictTask = calls.CodeById(codeId, (neuron, _) => neuron.Verify(codeId, code));
            if (verdictTask is null) { return false; }
            var verdict = await verdictTask;
            if (verdict.Outcome != CodeOutcome.Success) { return false; }
            if (!await calls.EmailIndex(hash, index => index.ProveChange(caller.PrincipalId, codeId))) { return false; }
        }

        if (!await calls.Credential(caller.PrincipalId, credential => credential.TryMoveEmailHash(entry.PreviousEmailHash, hash)))
        {
            await calls.EmailIndex(hash, index => index.CancelChange(caller.PrincipalId, codeId));
            return false;
        }

        if (!await calls.EmailIndex(hash, index => index.CommitChange(caller.PrincipalId, codeId)))
        {
            await calls.Credential(caller.PrincipalId, credential => credential.TryMoveEmailHash(hash, entry.PreviousEmailHash));
            return false;
        }

        await calls.EmailIndex(entry.PreviousEmailHash, index => index.Release(caller.PrincipalId));
        return true;
    }

    public async Task<bool> ChangePassword(string current, string next, CancellationToken cancellationToken = default)
    {
        if (host.CurrentCaller is not { PrincipalId.Length: > 0 } caller || !PasswordHasher.IsValidLength(next))
        {
            return false;
        }

        var check = await calls.Credential(caller.PrincipalId, credential => credential.CheckPassword(current));
        if (!check.Ok) { return false; }
        await calls.Credential(caller.PrincipalId, credential => credential.RotatePassword(new PasswordHasher(options.PasswordIterations).Hash(next)));
        return true;
    }
}
