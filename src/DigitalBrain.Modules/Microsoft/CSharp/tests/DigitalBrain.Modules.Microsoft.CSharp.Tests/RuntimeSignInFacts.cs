using System.Net.Http.Json;

namespace DigitalBrain.Modules.Microsoft.CSharp.Tests;

public sealed class RuntimeSignInFacts
{
    [Fact]
    public async Task SignInTouchesTheBrainRuntimeSoArmedBehaviorsAreAlive()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = new FakeSessionHost();
        await using var brain = await RuntimeBrain.StartAsync(host, ct);
        var acc = await brain.PersonAsync("acc", ct);
        await RuntimeBrain.Programs(brain, acc.BrainId).Assign(acc.Key("csharp/a"), RuntimeBrain.One(RuntimeBrain.Idle), []);
        await host.NextAsync(FakeSessionHost.IsStart, ct);
        host.Sessions.Clear();

        await SignInAsync(acc, ct);

        await host.NextAsync(FakeSessionHost.IsStart, ct);
        Assert.Single(host.Sessions);
    }

    [Fact]
    public async Task SignInToABrainWithoutBehaviorsStartsNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = new FakeSessionHost();
        await using var brain = await RuntimeBrain.StartAsync(host, ct);

        await SignInAsync(await brain.PersonAsync("acc", ct), ct);

        Assert.DoesNotContain(host.Requests, FakeSessionHost.IsStart);
    }

    private static async Task SignInAsync(Person person, CancellationToken ct)
    {
        using var signedIn = await person.Http.PostAsJsonAsync("/identity/login",
            new { principalId = person.PrincipalId, password = person.PrincipalId + "-password-123" }, ct);
        signedIn.EnsureSuccessStatusCode();
    }
}
