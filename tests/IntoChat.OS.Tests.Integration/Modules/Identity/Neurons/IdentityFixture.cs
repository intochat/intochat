using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using IntoChat.OS.Modules.Identity;
using IntoChat.OS.Modules.Identity.Accounts;
using IntoChat.OS.Modules.Identity.Authority;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;

namespace IntoChat.OS.Authority.Tests;

public sealed class IdentityFixture : BothRuntimesFixture
{
    public static TheoryData<NeuronRuntimeKind> Kinds => NeuronRuntimeKinds.Both;

    protected override IReadOnlyList<Type> Modules { get; } = [typeof(IdentityModule)];

    protected override void ConfigureTestOS(IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(Options.Create(new AuthOptions
        {
            Posture = IdentityPosture.Open,
            TrustedIssuers = [new TrustedIssuer { Name = "known-issuer", Audience = "https://os.local" }],
        }));
        services.TryAddSingleton<AuthorityMirror>();
    }

    protected override void ConfigureSilo(IServiceCollection services)
    {
        services.AddJournalPayloads(typeof(Grant).Assembly);
        services.AddJournalPayloads(typeof(IdentityModule).Assembly);
        // The module's signals (GrantsChanged, ContactsChanged) live in its contracts assembly, which a host registers through IdentityModule.Contracts.
        services.AddJournalPayloads(typeof(IntoChat.OS.Modules.Identity.Contracts.Grants.GrantsChanged).Assembly);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(Options.Create(new AuthOptions
        {
            Posture = IdentityPosture.Open,
            TrustedIssuers = [new TrustedIssuer { Name = "known-issuer", Audience = "https://os.local" }],
        }));
        services.TryAddSingleton<AuthorityMirror>();
        services.TryAddSingleton<AuthorityNeurons>();
        services.TryAddSingleton<IIdentityClient, LocalIdentityClient>();
    }
}
