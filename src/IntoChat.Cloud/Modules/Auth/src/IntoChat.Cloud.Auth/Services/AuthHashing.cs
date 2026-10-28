using System.Collections.Concurrent;
using System.Text;
using IntoChat.Cloud.Auth.Core;

namespace IntoChat.Cloud.Auth.Services;

// W4 D7 purpose hashing under the module's auth-hashing key. Purposes: auth-codes, auth-recovery, auth-email-subject,
// auth-session-handle, auth-ip. Key hashes (lowercase hex) are what neuron keys carry; never an address or a secret.
internal sealed class AuthHashing(AuthSecrets secrets)
{
    public const string Codes = "auth-codes";
    public const string EmailSubjectPurpose = "auth-email-subject";
    public const string SessionHandlePurpose = "auth-session-handle";
    public const string IpPurpose = "auth-ip";

    private readonly ConcurrentDictionary<string, PurposeHasher> hashers = new(StringComparer.Ordinal);
    private byte[]? rootKey;

    public byte[] RootKey => rootKey ??= LoadKey();

    public PurposeHasher For(string purpose) => hashers.GetOrAdd(purpose, name => new PurposeHasher(name, RootKey));

    // The HMAC as lowercase hex: safe inside grain keys and stable per (purpose, value).
    public string KeyHash(string purpose, string value)
    {
        var stored = For(purpose).Hash(value);
        return Convert.ToHexStringLower(Convert.FromBase64String(stored[(stored.LastIndexOf(':') + 1)..]));
    }

    public string EmailSubject(string normalizedEmail) => KeyHash(EmailSubjectPurpose, normalizedEmail);

    // What the sessions table stores and checks instead of the session id.
    public string SidHash(string sid) => KeyHash(SessionHandlePurpose, "sid:" + sid);

    // An opaque, listable session handle: the first 16 bytes of HMAC(sid), base64url. Never the sid itself.
    public string SessionHandle(string sid)
    {
        var stored = For(SessionHandlePurpose).Hash(sid);
        var bytes = Convert.FromBase64String(stored[(stored.LastIndexOf(':') + 1)..]);
        return Convert.ToBase64String(bytes, 0, 16).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private byte[] LoadKey()
    {
        // Hashing runs inside grain turns; blocking on the grain scheduler would deadlock, so the one-time read leaves it.
        var value = Task.Run(() => secrets.Named(AuthSecrets.HashingKey)).GetAwaiter().GetResult();
        if (string.IsNullOrWhiteSpace(value) || Encoding.UTF8.GetByteCount(value) < 32)
        {
            throw new InvalidOperationException("Auth hashing key is not configured (at least 32 bytes).");
        }

        return Encoding.UTF8.GetBytes(value);
    }
}
