using DigitalBrain.Access;
using DigitalBrain.Brains;
using DigitalBrain.Modules;

namespace DigitalBrain.Tests.Unit.Brains;

[Collection(nameof(CallerAdmissionFacts))]
public sealed class CallerAdmissionFacts : IDisposable
{
    private static readonly BrainId Alice = new("alice");
    private readonly ICallerAmbient? _previous = CallerAdmission.Callers;

    public void Dispose() => CallerAdmission.Callers = _previous;

    [Fact]
    public void APersonWithAnAppPrincipalIsNotAnAppForTheAppIdCheck()
    {
        CallerAdmission.Callers = new FixedAmbient(EdgeAuthority.CreateInternal().Person("app:notes", "acct_1", Alice));

        CallerAdmission.RequireNotAppCaller("apps refused");
        Assert.Throws<UnauthorizedAccessException>(() => CallerAdmission.RequireSameBrain("alice", appId: "notes"));
    }

    [Fact]
    public void AnAppMustMatchTheAppId()
    {
        var person = new Actor.Person("p", "acct_1");
        CallerAdmission.Callers = new FixedAmbient(EdgeAuthority.CreateInternal().App("notes", person, Alice));

        CallerAdmission.RequireSameBrain("alice", appId: "notes");
        Assert.Throws<UnauthorizedAccessException>(() => CallerAdmission.RequireSameBrain("alice", appId: "other"));
        Assert.Throws<UnauthorizedAccessException>(() => CallerAdmission.RequireNotAppCaller("apps refused"));
    }

    [Fact]
    public void NoCallerIsRefusedWhenTheAmbientIsEmpty()
    {
        CallerAdmission.Callers = new FixedAmbient(null);

        Assert.Throws<UnauthorizedAccessException>(() => CallerAdmission.RequireSameBrain("alice"));
        Assert.Throws<UnauthorizedAccessException>(() => CallerAdmission.RequireNotAppCaller("apps refused"));
    }

    [Fact]
    public void NoCallerIsRefusedWhenNoSourceIsSet()
    {
        CallerAdmission.Callers = null;

        Assert.Throws<UnauthorizedAccessException>(() => CallerAdmission.RequireSameBrain("alice"));
        Assert.Throws<UnauthorizedAccessException>(() => CallerAdmission.RequireNotAppCaller("apps refused"));
    }

    private sealed class FixedAmbient(Caller? current) : ICallerAmbient
    {
        public Caller? Current { get; } = current;
    }
}
