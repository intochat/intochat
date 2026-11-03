using System.Net;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;

namespace IntoChat.OS.Modules.Apps;

// The per-run bearer for PUT /dev/apps. The silo writes it when it becomes active. It is never logged.
internal sealed class DevAppGate(IConfiguration configuration, DigitalBrain.Runtime.Orleans.RuntimeOptions? runtimeOptions = null)
{
    internal string? Secret { get; private set; }
    internal string? TrustedGateway { get; } = configuration["IntoChatOS:Dev:TrustedGateway"];

    internal void Rotate()
    {
        var home = DigitalBrain.Runtime.Orleans.StateHome.Resolve(configuration, runtimeOptions ?? new() { HomeKey = "IntoChatOS:Home", DefaultHomeDirectoryName = ".intochat" });
        Secret = DevAppAccess.Rotate(Path.Combine(home.Path, "dev-token"));
    }

    internal static DevAppGate ForTests(string secret, string? trustedGateway = null)
        => new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["IntoChatOS:Dev:TrustedGateway"] = trustedGateway }).Build())
        { Secret = secret };
}

internal sealed class DevTokenLifecycle(DevAppGate gate, IConfiguration configuration) : ILifecycleParticipant<ISiloLifecycle>
{
    public void Participate(ISiloLifecycle lifecycle)
    {
        ArgumentNullException.ThrowIfNull(lifecycle);
        if (!DevAppAccess.IsLocal(configuration)) { return; }
        lifecycle.Subscribe<DevTokenLifecycle>(ServiceLifecycleStage.Active, _ =>
        {
            gate.Rotate();
            return Task.CompletedTask;
        });
    }
}

internal static class DevAppAccess
{
    internal const int MaxBody = 5 * 1024 * 1024;

    internal static bool IsLocal(IConfiguration configuration)
    {
        var topology = configuration["IntoChatOS:Topology"];
        return string.IsNullOrWhiteSpace(topology)
            || string.Equals(topology.Trim(), "local", StringComparison.OrdinalIgnoreCase);
    }

    // 400 host, 415 content type, 403 remote, 401 bearer. Null when the request may proceed.
    internal static int? Reject(string? host, string? contentType, IPAddress? remote, string? authorization, string secret, string? trustedGateway)
    {
        if (!HostAllowed(host)) { return StatusCodes.Status400BadRequest; }
        if (!IsJson(contentType)) { return StatusCodes.Status415UnsupportedMediaType; }
        if (!RemoteAllowed(remote, trustedGateway)) { return StatusCodes.Status403Forbidden; }
        if (!BearerMatches(authorization, secret)) { return StatusCodes.Status401Unauthorized; }
        return null;
    }

    internal static bool UnsafePath(string? path)
        => string.IsNullOrWhiteSpace(path)
            || Path.IsPathRooted(path)
            || path.Contains(':', StringComparison.Ordinal)
            || path.Split(['/', '\\']).Any(part => part is ".." or "." or "");

    internal static string Rotate(string path)
    {
        var secret = Base64Url(RandomNumberGenerator.GetBytes(32));
        var directory = Path.GetDirectoryName(path) ?? throw new ArgumentException("The dev token needs a folder.", nameof(path));
        Directory.CreateDirectory(directory);
        var temp = path + ".tmp";
        File.WriteAllText(temp, secret);
        Protect(temp);
        File.Move(temp, path, overwrite: true);
        return secret;
    }

    internal static bool HostAllowed(string? host)
    {
        if (string.IsNullOrWhiteSpace(host) || host.Contains('@')) { return false; }
        var name = host.Trim();
        if (name.StartsWith('['))
        {
            var end = name.IndexOf(']');
            if (end < 1) { return false; }
            name = name[1..end];
        }
        else
        {
            var colon = name.LastIndexOf(':');
            if (colon > 0 && name.IndexOf(':') == colon) { name = name[..colon]; }
        }
        return name is "localhost" or "127.0.0.1" or "::1";
    }

    internal static bool IsJson(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType)) { return false; }
        var media = contentType.Split(';')[0].Trim();
        return media.Equals("application/json", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool RemoteAllowed(IPAddress? remote, string? trustedGateway)
    {
        if (remote is null) { return false; }
        if (IPAddress.IsLoopback(remote)) { return true; }
        if (remote.IsIPv4MappedToIPv6 && IPAddress.IsLoopback(remote.MapToIPv4())) { return true; }
        if (string.IsNullOrWhiteSpace(trustedGateway)) { return false; }
        var configured = trustedGateway.Trim();
        if (IPAddress.TryParse(configured, out var address) && remote.Equals(address)) { return true; }
        return Uri.TryCreate(configured, UriKind.Absolute, out var uri)
            && IPAddress.TryParse(uri.Host, out address)
            && remote.Equals(address);
    }

    internal static bool BearerMatches(string? authorization, string secret)
    {
        const string prefix = "Bearer ";
        if (authorization is null || !authorization.StartsWith(prefix, StringComparison.Ordinal) || secret.Length == 0) { return false; }
        var token = authorization[prefix.Length..];
        var given = Encoding.UTF8.GetBytes(token);
        var expected = Encoding.UTF8.GetBytes(secret);
        return given.Length == expected.Length && CryptographicOperations.FixedTimeEquals(given, expected);
    }

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static void Protect(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var info = new FileInfo(path);
            var security = info.GetAccessControl();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            var user = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("The current user has no Windows identity.");
            security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
            info.SetAccessControl(security);
            return;
        }
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
