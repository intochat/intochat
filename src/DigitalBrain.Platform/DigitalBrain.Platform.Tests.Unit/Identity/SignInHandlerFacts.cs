using System.Collections.Concurrent;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Identity;
using DigitalBrain.Platform.Identity;
using DigitalBrain.Platform.Identity.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Platform.Tests.Unit.Identity;

public sealed class SignInHandlerFacts
{
    [Fact(Timeout = 120_000)]
    public async Task SignInRunsEverySignInHandlerAsTheMember()
    {
        var ct = TestContext.Current.CancellationToken;
        var recording = new RecordingHandler();
        await using var brain = await ModuleTest.Create().StartAsync(ct);
        await using var app = await StartAsync(brain, recording, ct);
        using var client = new HttpClient(new HttpClientHandler { CookieContainer = new() }) { BaseAddress = new(app.Urls.Single()) };
        await brain.Grains.GetGrain<IIdentityDirectory>(IdentityGrains.Directory).RegisterAsync("ada", "correct-password", "Ada", ct);

        (await client.PostAsJsonAsync("/identity/login", new { principalId = "ada", password = "correct-password" }, ct)).EnsureSuccessStatusCode();

        var (member, caller) = Assert.Single(recording.Calls);
        Assert.Equal("ada", member.PrincipalId);
        Assert.Equal((member.PrincipalId, member.AccountId, member.BrainId, CallerKind.User, TrustedEdge.AuthenticatedHttp),
            (caller!.PrincipalId, caller.AccountId, caller.BrainId, caller.Kind, caller.StampedBy));
    }

    [Fact(Timeout = 120_000)]
    public async Task AFailingSignInHandlerNeverFailsSignIn()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().StartAsync(ct);
        await using var app = await StartAsync(brain, new FailingHandler(), ct);
        using var client = new HttpClient(new HttpClientHandler { CookieContainer = new() }) { BaseAddress = new(app.Urls.Single()) };
        await brain.Grains.GetGrain<IIdentityDirectory>(IdentityGrains.Directory).RegisterAsync("bea", "correct-password", "Bea", ct);

        using var login = await client.PostAsJsonAsync("/identity/login", new { principalId = "bea", password = "correct-password" }, ct);

        login.EnsureSuccessStatusCode();
    }

    private static async Task<WebApplication> StartAsync(ModuleBrain brain, ISignInHandler handler, CancellationToken ct)
    {
        var builder = WebApplication.CreateBuilder();
        DigitalBrain.Testing.TestLogging.Apply(builder.Configuration);
        builder.Configuration[AuthOptions.PostureKey] = "Secured";
        builder.Services.AddSingleton<IDigitalBrain>(brain);
        builder.Services.AddSingleton<Orleans.IGrainFactory>(brain.Grains);
        builder.Services.AddSingleton(brain.SiloServices.GetRequiredService<DigitalBrain.Framework.Platform.Identity.IIdentityClient>());
        builder.Services.AddSingleton(brain.SiloServices.GetRequiredService<DigitalBrain.Platform.Identity.PrincipalPasswords>());
        builder.Services.AddSingleton(brain.SiloServices.GetRequiredService<DigitalBrain.Platform.Identity.BrainBirths>());
        builder.Services.AddSingleton(brain.SiloServices.GetRequiredService<IBrainAccess>());
        builder.Services.AddSingleton(handler);
        builder.Services.AddIdentity();
        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        app.UsePlatformHttp();
        IdentityEndpoints.Map(app);
        await app.StartAsync(ct);
        return app;
    }

    private sealed class RecordingHandler : ISignInHandler
    {
        public ConcurrentQueue<(Member Member, CallerContext? Caller)> Calls { get; } = new();

        public Task SignedIn(Member member, CancellationToken cancellationToken)
        {
            Calls.Enqueue((member, CallerContextStamper.TryGet(out var caller) ? caller : null));
            return Task.CompletedTask;
        }
    }

    private sealed class FailingHandler : ISignInHandler
    {
        public Task SignedIn(Member member, CancellationToken cancellationToken) => throw new InvalidOperationException("handler broke");
    }
}
