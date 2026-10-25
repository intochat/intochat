using DigitalBrain.Testing.Conformance;
using DigitalBrain.Testing.Conformance.Identity;
using DigitalBrain.Testing.Conformance.Secrets;

namespace DigitalBrain.Testing.Tests.Unit.Conformance;

// The reference subject: InMemorySecrets must pass every secrets@1 fact.
public sealed class InMemorySecretsConformance : SecretsConformance<InMemorySecrets>
{
    protected override Task<SecretsSubject> Connect(CancellationToken ct)
    {
        var callers = new AmbientCallerScope();
        var secrets = new InMemorySecrets(callers);
        return Task.FromResult(new SecretsSubject(secrets, callers, () => secrets.Log));
    }
}

// The TestOS secrets fake passes the kit too.
public sealed class FakeSecretsConformance : SecretsConformance<FakeSecrets>
{
    protected override Task<SecretsSubject> Connect(CancellationToken ct)
    {
        var callers = new AmbientCallerScope();
        var secrets = new FakeSecrets(callers);
        return Task.FromResult(new SecretsSubject(secrets, callers, () => secrets.Log));
    }
}

// The reference subject: InMemoryIdentity must pass every identity@1 fact.
public sealed class InMemoryIdentityConformance : IdentityConformance<InMemoryIdentity>
{
    protected override Task<IdentitySubject> Connect(CancellationToken ct)
    {
        var callers = new AmbientCallerScope();
        var identity = new InMemoryIdentity(callers);
        return Task.FromResult(new IdentitySubject(identity, identity, callers, identity.SignIn));
    }
}

// The TestOS identity fake passes the kit too.
public sealed class FakeIdentityConformance : IdentityConformance<FakeIdentity>
{
    protected override Task<IdentitySubject> Connect(CancellationToken ct)
    {
        var callers = new AmbientCallerScope();
        var identity = new FakeIdentity(callers);
        return Task.FromResult(new IdentitySubject(identity, identity, callers, identity.SignIn));
    }
}
