using DigitalBrain;
using DigitalBrain.Neurons;
using IntoChat.Cloud.Auth.Core;
using IntoChat.Cloud.Auth.Neurons;
using IntoChat.Cloud.Organisations;

namespace IntoChat.Cloud.Auth.Services;

// The one place Auth resolves neuron references (CV-1): typed proxies from the host's INeuronProxies, keyed by the os full
// key. Calls between Auth's own neurons are module-internal; user-facing checks (owner, Admin) stay inside the neurons.
internal sealed class AuthCalls(INeuronProxies neurons, IAuthHostSeam host)
{
    public Task<T> SystemAudit<T>(Func<IAuthSystemAudit, Task<T>> call) => Scoped(() => call(Get<IAuthSystemAudit>(IAuthSystemAudit.Type, IAuthSystemAudit.Key())));

    public Task SystemAudit(Func<IAuthSystemAudit, Task> call) => Scoped(() => call(Get<IAuthSystemAudit>(IAuthSystemAudit.Type, IAuthSystemAudit.Key())));

    public Task AuditLog(string orgBrain, Func<IAuthAuditLog, Task> call) => Scoped(() => call(Get<IAuthAuditLog>(IAuthAuditLog.Type, IAuthAuditLog.Key(orgBrain))));

    public Task<T> Membership<T>(string principalId, Func<IOrgMembership, Task<T>> call) => Scoped(() => call(Get<IOrgMembership>(IOrgMembership.Type, IOrgMembership.Key(principalId))));

    public Task<T> Code<T>(string subjectHash, CodePurpose purpose, Func<IAuthCode, Task<T>> call) => Scoped(() => call(Get<IAuthCode>(IAuthCode.Type, IAuthCode.KeyForSubject(subjectHash, purpose))));

    // The slot a code id routes to, or null for a malformed id (never resolves a neuron for it).
    public Task<T>? CodeById<T>(string codeId, Func<IAuthCode, CodePurpose, Task<T>> call) =>
        IAuthCode.KeyForCodeId(codeId, out var purpose) is { } key ? Scoped(() => call(Get<IAuthCode>(IAuthCode.Type, key), purpose)) : null;

    public Task<T> EmailIndex<T>(string emailHash, Func<IAuthEmailIndex, Task<T>> call) => Scoped(() => call(Get<IAuthEmailIndex>(IAuthEmailIndex.Type, IAuthEmailIndex.Key(emailHash))));

    public Task EmailIndex(string emailHash, Func<IAuthEmailIndex, Task> call) => Scoped(() => call(Get<IAuthEmailIndex>(IAuthEmailIndex.Type, IAuthEmailIndex.Key(emailHash))));

    public Task<T> Profile<T>(string principalId, Func<IAuthProfile, Task<T>> call) => Scoped(() => call(Get<IAuthProfile>(IAuthProfile.Type, IAuthProfile.Key(principalId))));

    public Task Profile(string principalId, Func<IAuthProfile, Task> call) => Scoped(() => call(Get<IAuthProfile>(IAuthProfile.Type, IAuthProfile.Key(principalId))));

    public Task<T> Credential<T>(string principalId, Func<IAuthCredential, Task<T>> call) => Scoped(() => call(Get<IAuthCredential>(IAuthCredential.Type, IAuthCredential.Key(principalId))));

    public Task Credential(string principalId, Func<IAuthCredential, Task> call) => Scoped(() => call(Get<IAuthCredential>(IAuthCredential.Type, IAuthCredential.Key(principalId))));

    public Task<T> Sessions<T>(string principalId, Func<IAuthSessions, Task<T>> call) => Scoped(() => call(Get<IAuthSessions>(IAuthSessions.Type, IAuthSessions.Key(principalId))));

    public Task Sessions(string principalId, Func<IAuthSessions, Task> call) => Scoped(() => call(Get<IAuthSessions>(IAuthSessions.Type, IAuthSessions.Key(principalId))));

    public Task<T> Directory<T>(string principalId, Func<IOrganisationDirectory, Task<T>> call) => Scoped(() => call(Get<IOrganisationDirectory>(IOrganisationDirectory.Type, IOrganisationDirectory.Key(principalId))));

    public Task<T> IpThrottle<T>(string ipHash, Func<IIpThrottle, Task<T>> call) => Scoped(() => call(Get<IIpThrottle>(IIpThrottle.Type, IIpThrottle.Key(ipHash))));

    // The call is a module call: a request from the HTTP edge still carries its person's stamp, and the runtime lets a stamped request
    // reach a module's [Internal] neurons only inside the host's module-call scope.
    private Task<T> Scoped<T>(Func<Task<T>> call) => host.InModuleCall(call);

    private async Task Scoped(Func<Task> call) => await Scoped(async () =>
    {
        await call();
        return true;
    });

    private T Get<T>(string type, string fullKey) where T : class => neurons.Get<T>(AuthIds.Of(type, fullKey));
}
