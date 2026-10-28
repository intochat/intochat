using DigitalBrain.Capabilities.Identity;
using IntoChat.Cloud.Auth.Core;
using IntoChat.Cloud.Auth.Licensing;
using IntoChat.Cloud.Auth.Services;
using Microsoft.Extensions.Hosting;

namespace IntoChat.Cloud.Auth;

// Runs only when Auth is composed: a licensed Auth demands the Secured identity posture and an absolute public URL. The
// privileged identity grants are in the module manifest, so the runtime enforces them (fail closed); there is no list to check.
// Messages name the setting, never its value.
internal sealed class AuthStartupGuard(
    AuthModuleOptions options,
    AuthLicenseState license,
    LicenseVerifier verifier,
    AuthAudit audit,
    IHostApplicationLifetime lifetime) : IHostedService
{
    internal const string PostureMessage =
        "Auth module is licensed but its identity posture is not Secured. Set IntoChatOS:Modules:auth:Options:Posture=Secured for this environment, or remove the auth licence.";

    internal const string PublicBaseUrlMessage =
        "Auth module is licensed but IntoChatOS:Modules:auth:Options:PublicBaseUrl is not set. Set it to the absolute URL users reach, or remove the auth licence.";

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (options.Posture != IdentityPosture.Secured)
        {
            throw new InvalidOperationException(PostureMessage);
        }

        if (string.IsNullOrWhiteSpace(options.PublicBaseUrl))
        {
            throw new InvalidOperationException(PublicBaseUrlMessage);
        }

        // The host gate already admitted the module; re-verifying here surfaces a swapped-in check as Degraded health.
        var verified = verifier.Verify(options.License);
        var status = verified.Ok ? "valid" : verified.Reason ?? LicenseFailure.Invalid;
        license.Set(verified.Org ?? "", verified.ExpiresAt, status);

        // The cluster serves neuron calls once the host has started; the system audit records the licence state then.
        lifetime.ApplicationStarted.Register(() => _ = audit.RecordAsync(null, AuthAuditKind.LicenseState, status, null, "start"));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
