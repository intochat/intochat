using IntoChat.OS.Framework;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities.Connections.Sealing;
using DigitalBrain.Capabilities.Secrets;
using DigitalBrain.Modules.Configuration;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Authority.Secrets;
using IntoChat.OS.Connections;
using IntoChat.OS.Framework.Modules;
using Microsoft.Extensions.DependencyInjection;
using SealedSecret = DigitalBrain.Capabilities.Secrets.SealedSecret;
using SecretRef = DigitalBrain.Capabilities.Secrets.SecretRef;
using SecretStatus = DigitalBrain.Capabilities.Secrets.SecretStatus;
using IntoChat.OS.Host.Callers;

namespace IntoChat.OS.Authority;

// secrets@1's vault port over the vault neuron. A caller stores in and removes from its own principal's vault.
public sealed class VaultPort(
    IServiceProvider? services,
    IBrainSecretSealer? sealer = null,
    Microsoft.Extensions.Configuration.IConfiguration? configuration = null,
    EdgeAuthority? edge = null,
    ICallerAmbient? callers = null) : ISecretVaultPort
{
    private Caller RequiredCaller()
        => (callers ?? services?.GetService<ICallerAmbient>())?.Current
            ?? throw new UnauthorizedAccessException("secrets@1: the call carries no caller.");

    // Whose vault the caller uses: the person it acts for, or a module's own.
    private static string? VaultOwner(Caller caller) => CallerRules.ActsFor(caller) ?? (caller.Actor is Actor.Module module ? module.Id : null);

    // A deployment secret (09-05): the installer carries a module's declared secret {s} in IntoChatOS:Modules:{module}:Secrets:{s}
    // (ManifestEnvironment.SecretVariable). The runtime checked the module's manifest declares it; a name "{s}:Key" is covered
    // by the declared "{s}". Unset when the deployment configured no value.
    public Task<SecretVaultResult> Named(string name, CancellationToken cancellationToken)
    {
        var caller = RequiredCaller();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var reference = SecretRef.NamedPrefix + name;
        return Task.FromResult(SecretVaultResult.Stored(string.IsNullOrEmpty(DeploymentValue(caller, name))
            ? SecretRef.Unset(reference, name)
            : new SecretRef { Reference = reference, Label = name, Status = SecretStatus.Set }));
    }

    // A module's own declared secret, or an app's.
    private string? DeploymentValue(Caller caller, string name)
    {
        var module = caller.Actor switch { Actor.Module m => m.Id, Actor.App a => a.Key, _ => null };
        if (string.IsNullOrWhiteSpace(module) || configuration is null) { return null; }
        var colon = name.IndexOf(':', StringComparison.Ordinal);
        var declared = colon < 0 ? name : name[..colon];
        return configuration[DigitalBrain.Modules.Configuration.ModuleConfiguration.ModuleSection(module) + ":Secrets:" + declared];
    }

    public async Task<SecretVaultResult> Set(string name, string label, string value, CancellationToken cancellationToken)
    {
        var caller = RequiredCaller();
        try
        {
            var stored = await AsPlatform(() => Vault(caller).Set(name, label, value).WaitAsync(cancellationToken)).ConfigureAwait(false);
            return SecretVaultResult.Stored(stored);
        }
        catch (InvalidOperationException) { return SecretVaultResult.Denied; }
    }

    public async Task<SecretVaultStatus> Remove(string name, CancellationToken cancellationToken)
    {
        var caller = RequiredCaller();
        try
        {
            await AsPlatform(async () => { await Vault(caller).Remove(name).WaitAsync(cancellationToken).ConfigureAwait(false); return true; }).ConfigureAwait(false);
            return SecretVaultStatus.Ok;
        }
        catch (InvalidOperationException) { return SecretVaultStatus.Denied; }
    }

    public async Task<SecretVaultResult> Resolve(SecretRef secret, CancellationToken cancellationToken)
    {
        var caller = RequiredCaller();
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
        if (!string.Equals(owner, VaultOwner(caller), StringComparison.Ordinal)) { return SecretVaultResult.Denied; }
        try
        {
            // The vault releases plaintext only to a trusted outbound call: the runtime, on behalf of the stamped caller's app.
            return SecretVaultResult.Resolved(await AsPlatform(() => Vault(caller).Resolve(reference).WaitAsync(cancellationToken)).ConfigureAwait(false));
        }
        catch (InvalidOperationException error) when (error.Message.Contains("another owner", StringComparison.Ordinal)) { return SecretVaultResult.Denied; }
        catch (InvalidOperationException) { return SecretVaultResult.NotFound; }
    }

    public async Task<SealedSecret?> Seal(string context, ReadOnlyMemory<byte> plaintext, CancellationToken cancellationToken)
    {
        var caller = RequiredCaller();
        cancellationToken.ThrowIfCancellationRequested();
        if (sealer is null || string.IsNullOrWhiteSpace(context)) { return null; }
        return await sealer.Seal(caller.Brain.Value, context, Bound(context, plaintext.Span)).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<byte[]?> Open(string context, SealedSecret sealedSecret, CancellationToken cancellationToken)
    {
        var caller = RequiredCaller();
        ArgumentNullException.ThrowIfNull(sealedSecret);
        cancellationToken.ThrowIfCancellationRequested();
        if (sealer is null || string.IsNullOrWhiteSpace(context)) { return null; }
        string? bound = null;
        // A tampered cipher, another key id or a value sealed elsewhere does not open: the capability answers Denied.
        try { bound = await sealer.Open(caller.Brain.Value, sealedSecret).WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (System.Security.Cryptography.CryptographicException) { return null; }
        catch (FormatException) { return null; }
        catch (ArgumentException) { return null; }
        var prefix = Prefix(context);
        return bound.StartsWith(prefix, StringComparison.Ordinal) ? Convert.FromBase64String(bound[prefix.Length..]) : null;
    }

    // A caller with no vault of its own (the kernel, the scheduler) is refused as the port refuses any other denial.
    private IOsVault Vault(Caller caller)
        => services is null ? throw new InvalidOperationException("The vault is not hosted.")
            : SiloNeurons.Get<IOsVault>(services, IOsVault.Key(VaultOwner(caller) ?? throw new InvalidOperationException("The caller has no vault.")));

    // The port made its own owner check; it reaches the vault as platform code, which the vault reads from the stamped caller.
    private Task<T> AsPlatform<T>(Func<Task<T>> call)
        => (edge ?? services?.GetService<EdgeAuthority>() ?? throw new InvalidOperationException("The vault is not hosted."))
            .AsKernel(BrainId.Kernel, call);

    // The sealer takes non-empty text: the context, a separator, then the bytes in base64 (empty bytes give an empty tail).
    private static string Prefix(string context) => context + "\n";

    private static string Bound(string context, ReadOnlySpan<byte> plaintext) => Prefix(context) + Convert.ToBase64String(plaintext);
}
