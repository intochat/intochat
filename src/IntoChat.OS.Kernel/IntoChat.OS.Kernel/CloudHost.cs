using System.Text;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Capabilities.Secrets;
using DigitalBrain.Runtime.Http;
using DigitalBrain.Runtime.Orleans.Capabilities;
using IntoChat.Cloud.Auth;
using IntoChat.Cloud.Auth.Licensing;
using IntoChat.Cloud.Auth.Services;
using IntoChat.Cloud.Auth.Ui;
using IntoChat.Cloud.Hosting;
using IntoChat.Cloud.Lobby;
using IntoChat.Cloud.Mail;
using IntoChat.Cloud.Mail.Templates;
using IntoChat.Cloud.Organisations;
using IntoChat.OS.Connections.Identity.Configuration;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Framework.Modules.Http;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Kernel;

// Services the cloud modules TryAdd. Registered on the host before module ConfigureServices, and only when Topology=cloud,
// so a local host never resolves IMailSender or the lobby token.
public static class CloudHost
{
    public static bool IsCloud(string? topology) => string.Equals(topology?.Trim(), "cloud", StringComparison.OrdinalIgnoreCase);

    public static void Register(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddForwardedHeadersSetup();
        services.TryAddSingleton<IAuthHostSeam, KernelAuthHost>();
        services.TryAddSingleton<IOrganisationHost, KernelOrganisationHost>();
        services.TryAddSingleton<IMailHost, KernelMailHost>();
        services.AddIdentitySessions<AuthIdentityProvider>();
        services.TryAddSingleton<ISessions>(provider => provider.GetRequiredKeyedService<ISessions>(Capability.Identity.V1Id));
        services.TryAddSingleton<AccountAuth>();
        services.TryAddSingleton<IAccountAuth>(provider => provider.GetRequiredService<AccountAuth>());
        services.TryAddSingleton<CloudSignIn>();
        services.TryAddSingleton<ILobbyAuth, CloudLobbyAuth>();
        services.TryAddSingleton<ILobbyTokenKey, SecretLobbyTokenKey>();
        services.TryAddSingleton<IOrganisationMail, CloudOrganisationMail>();
        services.TryAddSingleton<IOrganisationIdentity, CloudOrganisationIdentity>();
        services.AddOptions<IdentityHostOptions>().PostConfigure(IncludeAuth);
        services.AddOptions<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme)
            .PostConfigure<IServiceProvider>(ValidateSession);
    }

    // Auth's manifest holds the privileged grants. The identity options list is what IPrivilegedServices reads.
    private static void IncludeAuth(IdentityHostOptions options)
    {
        if (options.PrivilegedModules.Contains(AuthModule.Id, StringComparer.OrdinalIgnoreCase)) { return; }
        options.PrivilegedModules = [.. options.PrivilegedModules, AuthModule.Id];
    }

    private static void ValidateSession(CookieAuthenticationOptions cookie, IServiceProvider provider)
    {
        var previous = cookie.Events.OnValidatePrincipal;
        cookie.Events.OnValidatePrincipal = async context =>
        {
            if (previous is not null) { await previous(context).ConfigureAwait(false); }
            var sid = SessionPrincipal.SessionId(context.Principal);
            if (string.IsNullOrEmpty(sid) || context.HttpContext.RequestServices.GetService<ISessions>() is not { } sessions)
            {
                return;
            }

            try
            {
                Member? member;
                using (RuntimeCallerContext.Stamp(KernelAuthHost.Module))
                {
                    member = await sessions.Validate(sid, context.HttpContext.RequestAborted).ConfigureAwait(false);
                }

                if (member is null) { context.RejectPrincipal(); }
            }
            catch (UnauthorizedAccessException exception)
            {
                provider.GetService<ILoggerFactory>()?.CreateLogger(typeof(CloudHost)).LogWarning(exception, "Session validate refused");
                context.RejectPrincipal();
            }
        };
    }
}

public sealed class CloudLicenseCheck : ILicenseCheck
{
    public LicenseResult Check(ModuleId module, LicenseContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!string.Equals(module.Value, AuthModule.Id, StringComparison.OrdinalIgnoreCase))
        {
            return new LicenseResult(true, null, null, null);
        }

        var result = new AuthLicenseGate().Check(context.Configuration[AuthLicenseGate.TokenKey]);
        var reason = result.Failure is "missing" or "invalid" or "expired" ? result.Failure : null;
        return new LicenseResult(result.Ok, result.Holder, result.ExpiresAt, result.Ok ? null : reason ?? "invalid");
    }
}

internal sealed class KernelAuthHost : IAuthHostSeam
{
    internal static readonly CapabilityCaller Module = new(AuthModule.Id, AuthModule.Id, "system", AuthModule.Id);

    public CapabilityCaller? CurrentCaller => RuntimeCallerContext.Current;

    public IDisposable StampModuleCaller(CapabilityCaller module) => RuntimeCallerContext.Stamp(module);

    public Task<T> InModuleCall<T>(Func<Task<T>> call)
    {
        ArgumentNullException.ThrowIfNull(call);
        return call();
    }
}

internal sealed class KernelOrganisationHost : IOrganisationHost
{
    private static readonly CapabilityCaller Module = new(OrganisationsModule.Id, OrganisationsModule.Id, "system", OrganisationsModule.Id);

    public CapabilityCaller? CurrentCaller => RuntimeCallerContext.Current;

    public IDisposable StampModule() => RuntimeCallerContext.Stamp(Module);

    public async Task InModuleCall(Func<Task> call)
    {
        ArgumentNullException.ThrowIfNull(call);
        await call().ConfigureAwait(false);
    }

    public Task<T> InModuleCall<T>(Func<Task<T>> call)
    {
        ArgumentNullException.ThrowIfNull(call);
        return call();
    }
}

internal sealed class KernelMailHost : IMailHost
{
    private static readonly CapabilityCaller Module = new(MailModule.Id, MailModule.Id, "system", MailModule.Id);

    public IDisposable StampModule() => RuntimeCallerContext.Stamp(Module);
}

internal sealed class CloudLobbyAuth(CloudSignIn signIn) : ILobbyAuth
{
    public async Task<LobbyAuthResult> Submit(LobbyAuthCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var result = await signIn.Submit(command.Screen, command.VisitorId, command.Email, command.Secret, command.Purpose, command.Consent, cancellationToken).ConfigureAwait(false);
        return new LobbyAuthResult(result.Screen, result.Error, result.Member);
    }
}

internal sealed class SecretLobbyTokenKey(IServiceProvider services) : ILobbyTokenKey
{
    private byte[]? key;

    public byte[] Key => key ??= Read();

    private byte[] Read()
    {
        var secrets = services.GetRequiredKeyedService<ISecrets>(Capability.Secrets.V1Id);
        using var stamp = RuntimeCallerContext.Stamp(new CapabilityCaller(LobbyModule.Id, LobbyModule.Id, "system", LobbyModule.Id));
        var reference = secrets.Named(LobbyModule.TokenSecret).GetAwaiter().GetResult();
        if (reference.Status != SecretStatus.Set)
        {
            throw new InvalidOperationException("lobby-token-key is not configured.");
        }

        var value = secrets.Resolve(reference).GetAwaiter().GetResult();
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length < 32) { throw new InvalidOperationException("lobby-token-key must be at least 32 bytes."); }
        return bytes;
    }
}

internal sealed class CloudOrganisationMail(IMailSender sender) : IOrganisationMail
{
    public async Task<bool> SendTeamInvite(string email, string inviterName, string organisationName, string actionUrl, CancellationToken cancellationToken = default)
    {
        var result = await sender.SendAsync(Message(MailTemplateId.TeamInvite, email, inviterName, organisationName, actionUrl, member: null), cancellationToken).ConfigureAwait(false);
        return result.Outcome == MailSendOutcome.Accepted;
    }

    public Task SendInviteAccepted(string email, string memberName, string organisationName, string actionUrl, CancellationToken cancellationToken = default)
        => sender.SendAsync(Message(MailTemplateId.InviteAccepted, email, inviter: null, organisationName, actionUrl, memberName), cancellationToken);

    private static MailMessage Message(MailTemplateId template, string email, string? inviter, string organisationName, string actionUrl, string? member)
    {
        var data = new Dictionary<string, string>
        {
            [MailDataKeys.OrganisationName] = organisationName,
            [MailDataKeys.ActionUrl] = actionUrl,
        };
        if (inviter is not null) { data[MailDataKeys.InviterName] = inviter; }
        if (member is not null) { data[MailDataKeys.MemberName] = member; }
        return new MailMessage(template, email, "en", data);
    }
}

internal sealed class CloudOrganisationIdentity(IServiceProvider services) : IOrganisationIdentity
{
    private IIdentity Identity => services.GetRequiredKeyedService<IIdentity>(Capability.Identity.V1Id);

    public Task<Member?> Membership(string brainId, string principalId, CancellationToken cancellationToken = default)
        => Identity.Membership(brainId, principalId, cancellationToken);

    public Task<Member?> EnsureMember(Member member, CancellationToken cancellationToken = default)
        => Identity.EnsureMember(member, cancellationToken);

    public Task RemovePrincipalMember(string brainId, string principalId, CancellationToken cancellationToken = default)
        => Identity.RemovePrincipalMember(brainId, principalId, cancellationToken);
}
