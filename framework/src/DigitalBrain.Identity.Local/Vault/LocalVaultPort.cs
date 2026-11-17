using DigitalBrain.Access;
using System.Security.Cryptography;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Secrets;
using DigitalBrain.Identity.Local.Custody;
using DigitalBrain.Modules.Configuration;
using Microsoft.Extensions.Configuration;

namespace DigitalBrain.Identity.Local.Vault;

/// <summary>Implements <see cref="ISecretVaultPort"/> over <see cref="VaultNeuron"/> and <see cref="LocalSecretSealer"/>.</summary>
public sealed class LocalVaultPort(
    Func<Type, string, object> neuronResolver,
    IMasterKeyCustody custody,
    IConfiguration? configuration = null,
    EdgeAuthority? edge = null,
    ICallerAmbient? callers = null) : ISecretVaultPort
{
    private const string SecretScheme = "secret://";

    private readonly Func<Type, string, object> _neuronResolver = neuronResolver ?? throw new ArgumentNullException(nameof(neuronResolver));
    private readonly LocalSecretSealer _sealer = new(custody ?? throw new ArgumentNullException(nameof(custody)));
    private readonly IConfiguration? _configuration = configuration;

    /// <inheritdoc />
    public async Task<SecretVaultResult> Set(
        string name,
        string label,
        string value,
        CancellationToken cancellationToken)
    {
        var caller = Stamped();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var stored = await AsPlatform(() => Vault(caller).Set(name, label ?? "", value)).ConfigureAwait(false);
            return SecretVaultResult.Stored(stored);
        }
        catch (InvalidOperationException)
        {
            return SecretVaultResult.Denied;
        }
    }

    /// <inheritdoc />
    public async Task<SecretVaultStatus> Remove(
        string name,
        CancellationToken cancellationToken)
    {
        var caller = Stamped();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await AsPlatform(async () => { await Vault(caller).Remove(name).ConfigureAwait(false); return true; }).ConfigureAwait(false);
            return SecretVaultStatus.Ok;
        }
        catch (InvalidOperationException)
        {
            return SecretVaultStatus.Denied;
        }
    }

    /// <inheritdoc />
    public async Task<SecretVaultResult> Resolve(
        SecretRef secret,
        CancellationToken cancellationToken)
    {
        var caller = Stamped();
        ArgumentNullException.ThrowIfNull(secret);
        cancellationToken.ThrowIfCancellationRequested();
        if (secret.IsNamed)
        {
            var deployed = DeploymentValue(caller, secret.Reference[SecretRef.NamedPrefix.Length..]);
            return !string.IsNullOrEmpty(deployed)
                ? SecretVaultResult.Resolved(deployed)
                : SecretVaultResult.NotFound;
        }

        var reference = secret.Reference ?? "";
        if (!reference.StartsWith(SecretScheme, StringComparison.Ordinal))
        {
            return SecretVaultResult.NotFound;
        }

        var remainder = reference[SecretScheme.Length..];
        var slash = remainder.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0 || slash == remainder.Length - 1)
        {
            return SecretVaultResult.NotFound;
        }

        var owner = remainder[..slash];
        if (!string.Equals(owner, caller.For?.Principal, StringComparison.Ordinal))
        {
            return SecretVaultResult.Denied;
        }

        try
        {
            var resolved = await AsPlatform(() => Vault(caller).Resolve(secret)).ConfigureAwait(false);
            return SecretVaultResult.Resolved(resolved);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("another owner", StringComparison.OrdinalIgnoreCase))
        {
            return SecretVaultResult.Denied;
        }
        catch (InvalidOperationException)
        {
            return SecretVaultResult.NotFound;
        }
    }

    /// <inheritdoc />
    public async Task<SealedSecret?> Seal(
        string context,
        ReadOnlyMemory<byte> plaintext,
        CancellationToken cancellationToken)
    {
        var caller = Stamped();
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(context) || string.IsNullOrWhiteSpace(caller.Brain.Value))
        {
            return null;
        }

        var payload = context + "\n" + Convert.ToBase64String(plaintext.Span);
        return await _sealer.Seal(caller.Brain.Value, context, payload).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<byte[]?> Open(
        string context,
        SealedSecret sealedSecret,
        CancellationToken cancellationToken)
    {
        var caller = Stamped();
        ArgumentNullException.ThrowIfNull(sealedSecret);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(context) || string.IsNullOrWhiteSpace(caller.Brain.Value))
        {
            return null;
        }

        string bound;
        try
        {
            bound = await _sealer.Open(caller.Brain.Value, sealedSecret).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            return null;
        }

        var prefix = context + "\n";
        if (!bound.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            return Convert.FromBase64String(bound[prefix.Length..]);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public Task<SecretVaultResult> Named(
        string name,
        CancellationToken cancellationToken)
    {
        var caller = Stamped();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        cancellationToken.ThrowIfCancellationRequested();
        var reference = SecretRef.NamedPrefix + name;
        return Task.FromResult(SecretVaultResult.Stored(
            string.IsNullOrEmpty(DeploymentValue(caller, name))
                ? SecretRef.Unset(reference, name)
                : new SecretRef { Reference = reference, Label = name, Status = SecretStatus.Set }));
    }

    // The stamped caller; a vault belongs to a person, so a caller acting for no person reaches none.
    private Caller Stamped()
        => callers?.Current ?? edge?.Current ?? throw new UnauthorizedAccessException("secrets@1: the call carries no caller.");

    private IVault Vault(Caller caller)
        => (IVault)_neuronResolver(typeof(IVault), IVault.Key(caller.For?.Principal ?? throw new UnauthorizedAccessException("secrets@1: only a person, or an agent or app acting for one, has a vault.")));

    // The port made its own owner check; it reaches the vault as platform code in the kernel scope, which the vault reads from the
    // stamped caller. Without an EdgeAuthority there is no kernel scope, and the vault refuses.
    private async Task<T> AsPlatform<T>(Func<Task<T>> call)
    {
        using (edge?.EnterKernel(BrainId.Kernel))
        {
            return await call().ConfigureAwait(false);
        }
    }

    private string? DeploymentValue(Caller caller, string name)
    {
        var module = caller.Actor switch { Actor.Module m => m.Id, Actor.App a => a.Key, _ => null };
        if (_configuration is null || string.IsNullOrWhiteSpace(module) || string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return _configuration[ModuleConfiguration.ModuleSection(module) + ":Secrets:" + name];
    }
}
