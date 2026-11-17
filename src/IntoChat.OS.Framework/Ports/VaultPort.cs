using IntoChat.OS.Framework;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Connections.Sealing;
using DigitalBrain.Capabilities.Secrets;
using DigitalBrain.Modules.Configuration;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Authority.Secrets;
using IntoChat.OS.Connections;
using IntoChat.OS.Connections.Platform.Connections;
using IntoChat.OS.Framework.Modules;
using Microsoft.Extensions.DependencyInjection;
using SealedSecret = DigitalBrain.Capabilities.Secrets.SealedSecret;
using SecretRef = DigitalBrain.Capabilities.Secrets.SecretRef;
using SecretStatus = DigitalBrain.Capabilities.Secrets.SecretStatus;

namespace IntoChat.OS.Authority;

// secrets@1's vault port over the vault neuron. A caller stores in and removes from its own principal's vault.
public sealed class VaultPort(
    IServiceProvider? services,
    IBrainSecretSealer? sealer = null,
    Microsoft.Extensions.Configuration.IConfiguration? configuration = null,
    EdgeAuthority? edge = null) : ISecretVaultPort
{
    // A deployment secret (09-05): the installer carries a module's declared secret {s} in IntoChatOS:Modules:{module}:Secrets:{s}
    // (ManifestEnvironment.SecretVariable). The runtime checked the module's manifest declares it; a name "{s}:Key" is covered
    // by the declared "{s}". Unset when the deployment configured no value.
    public Task<SecretVaultResult> Named(CapabilityCaller caller, string name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var reference = SecretRef.NamedPrefix + name;
        return Task.FromResult(SecretVaultResult.Stored(string.IsNullOrEmpty(DeploymentValue(caller, name))
            ? SecretRef.Unset(reference, name)
            : new SecretRef { Reference = reference, Label = name, Status = SecretStatus.Set }));
    }

    private string? DeploymentValue(CapabilityCaller caller, string name)
    {
        if (string.IsNullOrWhiteSpace(caller.AppId) || configuration is null) { return null; }
        var colon = name.IndexOf(':', StringComparison.Ordinal);
        var declared = colon < 0 ? name : name[..colon];
        return configuration[DigitalBrain.Modules.Configuration.ModuleConfiguration.ModuleSection(caller.AppId) + ":Secrets:" + declared];
    }

    public async Task<SecretVaultResult> Set(CapabilityCaller caller, string name, string label, string value, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        try
        {
            var stored = await AsPlatform(() => Vault(caller).Set(name, label, value).WaitAsync(cancellationToken)).ConfigureAwait(false);
            return SecretVaultResult.Stored(stored);
        }
        catch (InvalidOperationException) { return SecretVaultResult.Denied; }
    }

    public async Task<SecretVaultStatus> Remove(CapabilityCaller caller, string name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        try
        {
            await AsPlatform(async () => { await Vault(caller).Remove(name).WaitAsync(cancellationToken).ConfigureAwait(false); return true; }).ConfigureAwait(false);
            return SecretVaultStatus.Ok;
        }
        catch (InvalidOperationException) { return SecretVaultStatus.Denied; }
    }

    public async Task<SecretVaultResult> Resolve(CapabilityCaller caller, SecretRef secret, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(secret);
        if (secret.IsNamed)
        {
            return DeploymentValue(caller, secret.Reference[SecretRef.NamedPrefix.Length..]) is { Length: > 0 } deployed
                ? SecretVaultResult.Resolved(deployed) : SecretVaultResult.NotFound;
        }

        if (!SecretReferences.IsReference(secret.Reference)) { return SecretVaultResult.NotFound; }
        var reference = SecretReferences.FromReference(secret.Reference, secret.Label);
        string owner;
        try { owner = reference.OwnerOf(); }
        catch (InvalidOperationException) { return SecretVaultResult.NotFound; }
        if (!string.Equals(owner, caller.PrincipalId, StringComparison.Ordinal)) { return SecretVaultResult.Denied; }
        try
        {
            // The vault releases plaintext only to a trusted outbound call: the runtime, on behalf of the stamped caller's app.
            return SecretVaultResult.Resolved(await AsPlatform(() => Vault(caller).Resolve(reference).WaitAsync(cancellationToken)).ConfigureAwait(false));
        }
        catch (InvalidOperationException error) when (error.Message.Contains("another owner", StringComparison.Ordinal)) { return SecretVaultResult.Denied; }
        catch (InvalidOperationException) { return SecretVaultResult.NotFound; }
    }

    public async Task<SealedSecret?> Seal(CapabilityCaller caller, string context, ReadOnlyMemory<byte> plaintext, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        cancellationToken.ThrowIfCancellationRequested();
        if (sealer is null || string.IsNullOrWhiteSpace(context)) { return null; }
        return await sealer.Seal(caller.BrainId, context, Bound(context, plaintext.Span)).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<byte[]?> Open(CapabilityCaller caller, string context, SealedSecret sealedSecret, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(sealedSecret);
        cancellationToken.ThrowIfCancellationRequested();
        if (sealer is null || string.IsNullOrWhiteSpace(context)) { return null; }
        string? bound = null;
        // A tampered cipher, another key id or a value sealed elsewhere does not open: the capability answers Denied.
        try { bound = await sealer.Open(caller.BrainId, sealedSecret).WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (System.Security.Cryptography.CryptographicException) { return null; }
        catch (FormatException) { return null; }
        catch (ArgumentException) { return null; }
        var prefix = Prefix(context);
        return bound.StartsWith(prefix, StringComparison.Ordinal) ? Convert.FromBase64String(bound[prefix.Length..]) : null;
    }

    private IOsVault Vault(CapabilityCaller caller)
        => services is null ? throw new InvalidOperationException("The vault is not hosted.")
            : SiloNeurons.Get<IOsVault>(services, IOsVault.Key(caller.PrincipalId));

    // The port made its own owner check; it reaches the vault as platform code, which the vault reads from the stamped caller.
    private Task<T> AsPlatform<T>(Func<Task<T>> call)
        => (edge ?? services?.GetService<EdgeAuthority>() ?? throw new InvalidOperationException("The vault is not hosted."))
            .AsKernel(BrainId.Kernel, call);

    // The sealer takes non-empty text: the context, a separator, then the bytes in base64 (empty bytes give an empty tail).
    private static string Prefix(string context) => context + "\n";

    private static string Bound(string context, ReadOnlySpan<byte> plaintext) => Prefix(context) + Convert.ToBase64String(plaintext);
}
