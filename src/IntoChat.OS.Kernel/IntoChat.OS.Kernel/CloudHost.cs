using System.Collections.Concurrent;
using System.Text;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Capabilities.Secrets;
using DigitalBrain.Modules.Options;
using DigitalBrain.Runtime.Http;
using DigitalBrain.Runtime.Http.Surface;
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
using IntoChat.OS.Authority.Identity;
using IntoChat.OS.Connections.Platform.Identity;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using OsIdentity = IntoChat.OS.Framework.Modules.Platform.Identity;

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
        AuthCloudRegistration.Register(services);
        services.TryAddSingleton<ILobbyAuth, CloudLobbyAuth>();
        services.TryAddSingleton<ILobbyTokenKey, SecretLobbyTokenKey>();
        services.TryAddSingleton<IOrganisationMail, CloudOrganisationMail>();
        services.TryAddSingleton<IOrganisationIdentity, CloudOrganisationIdentity>();
        services.TryAddSingleton<IBrainAccess>(provider => new CloudGuestTryBrainAccess(
            IdentityHosting.LocalBrainAccess(provider),
            provider.GetRequiredService<GuestTrySessionService>(),
            provider.GetService<TimeProvider>() ?? TimeProvider.System));
        services.AddOptions<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme)
            .PostConfigure<IServiceProvider>(ValidateSession);
    }

    public static WebApplication UseGuestTrySessions(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var provisionedBrains = new ConcurrentDictionary<string, GuestBrainEntry>(StringComparer.Ordinal);
        app.Use(async (context, next) =>
        {
            // Shut by default: a try token makes a guest (and its brain) only on an endpoint marked for guests. Anywhere
            // else the request stays anonymous and the account session answers 401.
            if (context.User.Identity?.IsAuthenticated != true
                && context.GetEndpoint()?.Metadata.GetMetadata<GuestSurface.Door>() is not null
                && TryMatchGuestBrainPath(context.Request.Path, out var targetBrainId)
                && GuestTryHttpSurface.ExtractToken(context.Request) is { Length: > 0 } rawToken
                && context.RequestServices.GetService<GuestTrySessionService>() is { } trySessions
                && context.RequestServices.GetService<ILobbyTokenKey>() is { } keys)
            {
                var now = (context.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow();
                var remoteIp = context.Connection.RemoteIpAddress?.ToString();
                if (trySessions.TryGetActive(remoteIp, rawToken, keys.Key, now, out var session)
                    && session is not null
                    && string.Equals(session.BrainId, targetBrainId, StringComparison.Ordinal))
                {
                    // The lobby names the brain, and the brain id is the guest's principal. A session the walls would not
                    // know as a guest's is not admitted.
                    var guest = CapabilityCallers.Of(session.BrainId, session.BrainId, session.BrainId);
                    if (GuestSurface.Is(guest))
                    {
                        await EnsureGuestBrainAsync(context.RequestServices, session, now, provisionedBrains, context.RequestAborted).ConfigureAwait(false);
                        context.User = AccountSession.SessionUser(guest);
                    }
                }
            }

            await next(context).ConfigureAwait(false);
        });
        return app;
    }

    private static async Task EnsureGuestBrainAsync(
        IServiceProvider services,
        GuestTrySession session,
        DateTimeOffset now,
        ConcurrentDictionary<string, GuestBrainEntry> provisionedBrains,
        CancellationToken cancellationToken)
    {
        foreach (var pair in provisionedBrains)
        {
            if (pair.Value.ExpiresAt <= now && provisionedBrains.TryRemove(pair.Key, out var expired))
            {
                expired.Gate.Dispose();
            }
        }

        var entry = provisionedBrains.GetOrAdd(session.BrainId, static (_, exp) => new GuestBrainEntry(exp), session.ExpiresAt);
        if (entry.Initialized && entry.ExpiresAt > now)
        {
            return;
        }

        await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (entry.Initialized && entry.ExpiresAt > now)
            {
                return;
            }

            if (services.GetService<IIdentityClient>() is { } identity)
            {
                await identity.EnsureAccount(new OsIdentity.Account
                {
                    AccountId = session.BrainId,
                    OwnerPrincipalId = session.BrainId,
                    Name = "Guest Try",
                    CreatedAt = session.IssuedAt,
                }).WaitAsync(cancellationToken).ConfigureAwait(false);

                var provision = await identity.EnsureBrain(new OsIdentity.Member
                {
                    PrincipalId = session.BrainId,
                    AccountId = session.BrainId,
                    BrainId = session.BrainId,
                    Role = OsIdentity.MemberRole.Owner,
                    DisplayName = "Guest",
                    JoinedAt = session.IssuedAt,
                }).WaitAsync(cancellationToken).ConfigureAwait(false);

                if (services.GetService<IBrainBirths>() is { } births)
                {
                    await births.Born(provision).WaitAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            entry.ExpiresAt = session.ExpiresAt;
            entry.Initialized = true;
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    private sealed class GuestBrainEntry(DateTimeOffset expiresAt)
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public DateTimeOffset ExpiresAt { get; set; } = expiresAt;
        public volatile bool Initialized;
    }

    internal static bool TryMatchGuestBrainPath(PathString path, out string brainId)
    {
        brainId = string.Empty;
        if (!path.StartsWithSegments("/brains", out var remaining) || !remaining.HasValue)
        {
            return false;
        }

        var trimmed = remaining.Value!.TrimStart('/');
        var slash = trimmed.IndexOf('/');
        var candidate = slash < 0 ? trimmed : trimmed[..slash];
        if (!candidate.StartsWith(GuestTryToken.BrainIdPrefix, StringComparison.Ordinal)
            || !DigitalBrain.Brains.BrainScope.IsValidId(candidate))
        {
            return false;
        }

        brainId = candidate;
        return true;
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
                using (ModuleScope.EnterCapability(KernelAuthHost.Module))
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
    internal static readonly CapabilityCaller Module = CapabilityCallers.Of(AuthModule.Id, AuthModule.Id, "system", CallerKind.App, AuthModule.Id);

    public CapabilityCaller? CurrentCaller => RuntimeCallerContext.Current;

    public IDisposable StampModuleCaller(CapabilityCaller module) => ModuleScope.EnterCapability(module);

    public async Task<T> InModuleCall<T>(Func<Task<T>> call)
    {
        ArgumentNullException.ThrowIfNull(call);
        using var scope = ModuleScope.EnterKernelCall();
        return await call().ConfigureAwait(false);
    }
}

internal sealed class KernelOrganisationHost : IOrganisationHost
{
    internal static readonly CapabilityCaller Module = CapabilityCallers.Of(OrganisationsModule.Id, OrganisationsModule.Id, "system", CallerKind.App, OrganisationsModule.Id);

    public CapabilityCaller? CurrentCaller => RuntimeCallerContext.Current;

    public IDisposable StampModule() => ModuleScope.EnterCapability(Module);

    public async Task InModuleCall(Func<Task> call)
    {
        ArgumentNullException.ThrowIfNull(call);
        using var scope = ModuleScope.EnterKernelCall();
        await call().ConfigureAwait(false);
    }

    public async Task<T> InModuleCall<T>(Func<Task<T>> call)
    {
        ArgumentNullException.ThrowIfNull(call);
        using var scope = ModuleScope.EnterKernelCall();
        return await call().ConfigureAwait(false);
    }
}

internal sealed class KernelMailHost : IMailHost
{
    internal static readonly CapabilityCaller Module = CapabilityCallers.Of(MailModule.Id, MailModule.Id, "system", CallerKind.App, MailModule.Id);

    public IDisposable StampModule() => ModuleScope.EnterCapability(Module);

    public async Task<T> InModuleCall<T>(Func<Task<T>> call)
    {
        ArgumentNullException.ThrowIfNull(call);
        using var scope = ModuleScope.EnterKernelCall();
        return await call().ConfigureAwait(false);
    }
}

internal sealed class ModuleScope : IDisposable
{
    private readonly IDisposable inner;
    private readonly object? previous;
    private bool disposed;

    private ModuleScope(IDisposable inner)
    {
        this.inner = inner;
        previous = Orleans.Runtime.RequestContext.Get(RuntimeCallerContext.ModuleCallKey);
        Orleans.Runtime.RequestContext.Set(RuntimeCallerContext.ModuleCallKey, true);
    }

    public static IDisposable EnterCapability(CapabilityCaller module)
    {
        var inProcess = InProcessCallerScope.Enter();
        var stamped = RuntimeCallerContext.Stamp(module);
        return new ModuleScope(new CompositeScope(stamped, inProcess));
    }

    public static IDisposable EnterKernelCall()
        => new ModuleScope(InProcessCallerScope.EnterKernel("system"));

    public void Dispose()
    {
        if (disposed) { return; }
        disposed = true;
        if (previous is null)
        {
            Orleans.Runtime.RequestContext.Remove(RuntimeCallerContext.ModuleCallKey);
        }
        else
        {
            Orleans.Runtime.RequestContext.Set(RuntimeCallerContext.ModuleCallKey, previous);
        }
        inner.Dispose();
    }

    private sealed class CompositeScope(IDisposable first, IDisposable second) : IDisposable
    {
        public void Dispose()
        {
            first.Dispose();
            second.Dispose();
        }
    }
}

internal sealed class CloudLobbyAuth(ICloudSignIn signIn) : ILobbyAuth
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
    internal static readonly CapabilityCaller Module = CapabilityCallers.Of(LobbyModule.Id, LobbyModule.Id, "system", CallerKind.App, LobbyModule.Id);
    private byte[]? key;

    public byte[] Key => key ??= Read();

    private byte[] Read()
    {
        var secrets = services.GetRequiredKeyedService<ISecrets>(Capability.Secrets.V1Id);
        using var scope = ModuleScope.EnterCapability(Module);
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
    private IIdentityClient Identity => services.GetRequiredService<IIdentityClient>();

    public async Task<Member?> Membership(string brainId, string principalId, CancellationToken cancellationToken = default)
    {
        var member = await Identity.Membership(brainId, principalId).WaitAsync(cancellationToken).ConfigureAwait(false);
        return member is null ? null : ToCapabilityMember(member);
    }

    public async Task<Member?> EnsureMember(Member member, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(member);
        var ensured = await Identity.EnsureMember(new OsIdentity.Member
        {
            PrincipalId = member.PrincipalId,
            AccountId = member.AccountId,
            BrainId = member.BrainId,
            Role = (OsIdentity.MemberRole)(int)member.Role,
            DisplayName = member.DisplayName,
            JoinedAt = member.JoinedAt,
        }).WaitAsync(cancellationToken).ConfigureAwait(false);
        return ensured is null ? null : ToCapabilityMember(ensured);
    }

    public Task RemovePrincipalMember(string brainId, string principalId, CancellationToken cancellationToken = default)
        => Identity.RemovePrincipalMember(brainId, principalId).WaitAsync(cancellationToken);

    private static Member ToCapabilityMember(OsIdentity.Member member) => new()
    {
        PrincipalId = member.PrincipalId,
        AccountId = member.AccountId,
        BrainId = member.BrainId,
        Role = (MemberRole)(int)member.Role,
        DisplayName = member.DisplayName,
        JoinedAt = member.JoinedAt,
    };
}

internal sealed class CloudGuestTryBrainAccess(IBrainAccess inner, GuestTrySessionService trySessions, TimeProvider clock) : IBrainAccess
{
    public ValueTask<bool> CanAccessAsync(string principalId, string accountId, string brainId, CancellationToken cancellationToken = default)
    {
        if (brainId.StartsWith(GuestTryToken.BrainIdPrefix, StringComparison.Ordinal)
            && string.Equals(principalId, brainId, StringComparison.Ordinal)
            && string.Equals(accountId, brainId, StringComparison.Ordinal)
            && brainId.Length > GuestTryToken.BrainIdPrefix.Length)
        {
            var sessionId = brainId[GuestTryToken.BrainIdPrefix.Length..];
            if (trySessions.IsActiveSession(sessionId, brainId, clock.GetUtcNow()))
            {
                return new(true);
            }
        }

        return inner.CanAccessAsync(principalId, accountId, brainId, cancellationToken);
    }
}
