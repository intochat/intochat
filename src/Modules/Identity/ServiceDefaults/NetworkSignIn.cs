using Microsoft.Extensions.Configuration;

namespace IntoChat.OS.ServiceDefaults;

// Sign-in on a kernel that listens beyond loopback. Unlike the Open-posture check (OpenPostureLoopback), this holds in every
// topology and in a container: the storage a brain runs on says nothing about who can reach it.
//
//   loopback only                      as configured; the built-in registration stays.
//   network, a sign-in module present  as configured; the module owns sign-in and registration.
//   network, no sign-in module         the built-in registration is closed, and the kernel starts only with the bootstrap
//                                      credential (one owner, no way for a visitor to make another) or the explicit opt-in.
//
// Decide is the one place that says so. The identity host asks it twice (Authority.Identity.NetworkSignInGuard): over the
// listeners configuration names, before anything binds, and over the addresses the server really bound.
public static class NetworkSignIn
{
    public const string UsernameKey = "IntoChatOS:Auth:Username";
    public const string PasswordKey = "IntoChatOS:Auth:Password";

    // Starts: whether the kernel may serve. OpenRegistration: whether the built-in registration route exists.
    public sealed record Verdict(bool Starts, bool OpenRegistration, string? Refusal);

    // The listeners configuration names beyond loopback, as the Open-posture check reads them, IntoChatOS:Bind included.
    public static IReadOnlyList<string> Exposed(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var exposed = OpenPostureLoopback.NonLoopbackConfigured(configuration).ToList();
        if (BindPolicy.From(configuration).HasNonLoopback) { exposed.Add($"{BindPolicy.BindKey} beyond loopback"); }
        return exposed;
    }

    public static Verdict Decide(IReadOnlyList<string> exposed, bool signInModule, bool builtInPasswordEndpoints, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(exposed);
        ArgumentNullException.ThrowIfNull(configuration);
        if (exposed.Count == 0 || signInModule) { return new(true, builtInPasswordEndpoints && exposed.Count == 0, null); }
        if (HasBootstrapCredential(configuration) || configuration.GetValue(OpenPostureLoopback.AllowOpenOnNetworkKey, false)) { return new(true, false, null); }
        return new(false, false,
            "This kernel listens beyond loopback (" + string.Join(", ", exposed) + ") and no sign-in module is present, so nothing decides who may use it. "
            + $"Configure the bootstrap credential ({UsernameKey} and {PasswordKey}) for a single owner, "
            + $"or set {OpenPostureLoopback.AllowOpenOnNetworkKey}=true to accept network exposure without one. To stay local, bind 127.0.0.1/localhost.");
    }

    public static bool HasBootstrapCredential(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return !string.IsNullOrEmpty(configuration[UsernameKey]) && !string.IsNullOrEmpty(configuration[PasswordKey]);
    }
}
