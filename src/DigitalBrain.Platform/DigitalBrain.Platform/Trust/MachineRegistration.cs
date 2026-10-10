using DigitalBrain.Framework.Platform.Identity;
using DigitalBrain.Platform.Identity;
using DigitalBrain.Platform.Identity.Configuration;
using Microsoft.Extensions.Options;

namespace DigitalBrain.Platform.Trust;

// R53/R59: at silo start, before any other startup work reads the kernel, a machine puts the account it serves on
// record at the trust side and registers itself for it: the Open posture's default owner (the same provisioning
// an authorized call would do lazily) or the Secured bootstrap user with its account and default brain. It never
// waits for anyone to sign in, so a fresh store boots.
internal sealed class MachineRegistration(MachineRegistrar registrar, IIdentityClient identity, OwnerBirth birth,
    IOptions<AuthOptions> auth, TimeProvider clock) : IStartupTask
{
    // Ahead of the Active-stage startup tasks (feature boot, seeders) that may read a brain's kernel.
    public const int Stage = ServiceLifecycleStage.Active - 1;

    public async Task Execute(CancellationToken cancellationToken)
    {
        var options = auth.Value;
        if (options.Posture == IdentityPosture.Open)
        {
            var owner = AccountSession.DefaultLogin;
            // Binding first: the signed provisioning is honoured only from a machine bound to the account (R63).
            await registrar.Bind(owner);
            birth.Await(await identity.ProvisionOwner(new Member { PrincipalId = owner, AccountId = owner, BrainId = owner, Role = MemberRole.Owner, DisplayName = owner }));
            return;
        }
        // Basic bootstrap is an explicit owner of one default scope, never a wildcard bypass.
        if (options.Username is not { Length: > 0 } principal || string.IsNullOrEmpty(options.Password)) { return; }
        var now = clock.GetUtcNow();
        await registrar.Bind(principal);
        await identity.EnsureAccount(new Account { AccountId = principal, OwnerPrincipalId = principal, Name = principal, CreatedAt = now });
        var provision = await identity.EnsureBrain(new Member
        {
            PrincipalId = principal, AccountId = principal, BrainId = AccountSession.DefaultLogin,
            DisplayName = principal, Role = MemberRole.Owner, JoinedAt = now,
        });
        birth.Await(provision);
    }
}

// A fresh store's owner brain is born once the silo is up, in the background (finding b, round 3 f): its creation
// handlers (the OS defaults, the features) need the seeded integrations and may run long, so they neither block
// the silo's start nor run before the Active-stage seeders.
// R73: with the part log on, the brain is born only on the machine that acquires its core. A machine refused the
// core (another own machine holds it, or its custody cannot open the core's key) has registered itself and stops
// here; whichever machine holds the core gives the birth, so the trust side's NewBrain winner need not be it.
internal sealed class OwnerBirth(BrainBirths births, IServiceProvider services, Microsoft.Extensions.Logging.ILogger<OwnerBirth> logger) : IStartupTask
{
    public const int Stage = ServiceLifecycleStage.Active + 1;

    private BrainProvision? _provision;

    public Task Born { get; private set; } = Task.CompletedTask;

    public void Await(BrainProvision provision) => _provision = provision;

    public Task Execute(CancellationToken cancellationToken)
    {
        if (_provision is not { } provision) { return Task.CompletedTask; }
        var logs = services.GetService(typeof(Parts.PartLogs)) as Parts.PartLogs;
        if (logs is null && !provision.NewBrain) { return Task.CompletedTask; }
        Born = Task.Run(async () =>
        {
            try
            {
                if (logs is not null)
                {
                    var core = DigitalBrain.Framework.Parts.PartId.Core(DigitalBrain.Framework.BrainScope.Create(provision.Member.AccountId, provision.Member.BrainId).Id);
                    try { await logs.For(core); }
                    catch (Exception refused) when (refused is ServiceUnavailableException or UnauthorizedAccessException)
                    {
                        Microsoft.Extensions.Logging.LoggerExtensions.LogInformation(logger,
                            "This machine does not hold the core of brain {Brain}; its birth is left to the machine that does: {Reason}", core.BrainId, refused.Message);
                        return;
                    }
                    // Create is idempotent: on the core's holder a brain that already exists is left as it is.
                    provision = provision with { NewBrain = true };
                }
                await births.Born(provision);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            { Microsoft.Extensions.Logging.LoggerExtensions.LogError(logger, error, "The owner brain {Brain} was not born at boot.", provision.Member.BrainId); }
        }, CancellationToken.None);
        return Task.CompletedTask;
    }
}
