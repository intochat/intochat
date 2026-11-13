using DigitalBrain.Capabilities.Identity;
using IntoChat.Cloud.Auth.Core;

namespace IntoChat.Cloud.Auth.Services;

// Trusted server callers only: the caller must verify ownership of the email before invoking provisioning.
public interface IAccountProvisioning
{
    Task<Member> EnsureAccount(string email, string displayName, CancellationToken ct = default);
}

internal sealed class VerifiedAccountProvisioning(AccountProvisioner provisioner) : IAccountProvisioning
{
    public async Task<Member> EnsureAccount(string email, string displayName, CancellationToken ct = default)
    {
        var normalized = EmailNormalizer.Normalize(email) ?? throw new ArgumentException("A valid email is required.", nameof(email));
        ct.ThrowIfCancellationRequested();
        return await provisioner.EnsureVerifiedEmailAsync(normalized, displayName).WaitAsync(ct)
            ?? throw new InvalidOperationException("Account provisioning is unavailable.");
    }
}
