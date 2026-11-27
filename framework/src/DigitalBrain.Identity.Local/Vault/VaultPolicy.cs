using DigitalBrain.Access;

namespace DigitalBrain.Identity.Local.Vault;

/// <summary>
/// Centralized, fail-closed vault owner resolution and authorization policy shared across
/// vault neurons, vault ports, and secret value sources.
/// </summary>
public static class VaultPolicy
{
    /// <summary>
    /// Resolves the vault owner identifier for a vault operation (Set/Remove/Resolve):
    /// <list type="bullet">
    /// <item><see cref="Actor.Person"/>: the person's principal.</item>
    /// <item><see cref="Actor.Agent"/>: <c>agent.For.Principal</c>.</item>
    /// <item><see cref="Actor.App"/>: <c>app.For.Principal</c> (for outbound Resolve only).</item>
    /// <item><see cref="Actor.Module"/>: <c>module.Id</c>.</item>
    /// </list>
    /// Returns <see langword="null"/> for callers that do not own a vault (such as <see cref="Actor.Kernel"/> or <see cref="Actor.Scheduler"/>).
    /// </summary>
    public static string? VaultOwner(Caller? caller) => caller?.Actor switch
    {
        Actor.Person person => person.Principal,
        Actor.Agent agent => agent.For.Principal,
        Actor.App app => app.For.Principal,
        Actor.Module module => module.Id,
        _ => null,
    };

    /// <summary>
    /// Resolves the vault owner whose secret values should be redacted for <paramref name="caller"/>.
    /// <see cref="Actor.App"/> is explicitly excluded (returns <see langword="null"/>; callers must also enforce <see cref="IsAppCaller"/>).
    /// </summary>
    public static string? RedactionOwner(Caller? caller) => caller?.Actor switch
    {
        Actor.Person person => person.Principal,
        Actor.Agent agent => agent.For.Principal,
        Actor.Module module => module.Id,
        Actor.Kernel when !string.IsNullOrWhiteSpace(caller.Brain.Value) => caller.Brain.Value,
        _ => null,
    };

    /// <summary>Returns <see langword="true"/> when <paramref name="caller"/> is an app principal.</summary>
    public static bool IsAppCaller(Caller? caller) => caller?.Actor is Actor.App;

    /// <summary>Returns <see langword="true"/> when <paramref name="caller"/> is the kernel.</summary>
    public static bool IsKernel(Caller? caller) => caller?.Actor is Actor.Kernel;

    /// <summary>
    /// Fail-closed allowlist for mutating a vault via <c>Set</c>:
    /// allows <see cref="Actor.Kernel"/>, a direct <see cref="Actor.Person"/> owning the vault (<c>Direct == true</c>),
    /// an <see cref="Actor.Agent"/> acting for the owner, or an <see cref="Actor.Module"/> storing into its own vault.
    /// Apps, relayed persons, schedulers, other principals, and unstamped callers are refused.
    /// </summary>
    public static bool CanStore(Caller? caller, string owner)
    {
        if (caller is null || string.IsNullOrWhiteSpace(owner))
        {
            return false;
        }

        return caller.Actor switch
        {
            Actor.Kernel => true,
            Actor.Person person => caller.Direct && string.Equals(person.Principal, owner, StringComparison.Ordinal),
            Actor.Agent agent => string.Equals(agent.For.Principal, owner, StringComparison.Ordinal),
            Actor.Module module => string.Equals(module.Id, owner, StringComparison.Ordinal),
            _ => false,
        };
    }

    /// <summary>
    /// Fail-closed allowlist for removing a secret from a vault via <c>Remove</c>:
    /// allows <see cref="Actor.Kernel"/>, a direct <see cref="Actor.Person"/> owning the vault (<c>Direct == true</c>),
    /// or an <see cref="Actor.Module"/> removing from its own vault.
    /// </summary>
    public static bool CanRemove(Caller? caller, string owner)
    {
        if (caller is null || string.IsNullOrWhiteSpace(owner))
        {
            return false;
        }

        return caller.Actor switch
        {
            Actor.Kernel => true,
            Actor.Person person => caller.Direct && string.Equals(person.Principal, owner, StringComparison.Ordinal),
            Actor.Module module => string.Equals(module.Id, owner, StringComparison.Ordinal),
            _ => false,
        };
    }

    /// <summary>
    /// Fail-closed allowlist for resolving a secret's plaintext value:
    /// allows <see cref="Actor.Kernel"/> or an <see cref="Actor.App"/> acting for the vault's owner.
    /// </summary>
    public static bool CanResolve(Caller? caller, string owner)
    {
        if (caller is null || string.IsNullOrWhiteSpace(owner))
        {
            return false;
        }

        return caller.Actor switch
        {
            Actor.Kernel => true,
            Actor.App app => string.Equals(app.For.Principal, owner, StringComparison.Ordinal),
            _ => false,
        };
    }

    /// <summary>
    /// Fail-closed allowlist for listing raw secret values for redaction: strictly <see cref="Actor.Kernel"/> only.
    /// </summary>
    public static bool CanReadValues(Caller? caller) => caller?.Actor is Actor.Kernel;
}
