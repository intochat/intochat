using DigitalBrain;
using System.Net;
using System.Net.Http.Json;
using DigitalBrain.Access;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Runtime.Orleans.Capabilities;
using IntoChat.OS.Authority.Identity;
using IntoChat.OS.Kernel.Mcp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using DigitalBrain.Runtime.Http.Surface;
using IntoChat.OS.Host.Surface;
using IntoChat.OS.Modules.Identity.Accounts;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;
using IntoChat.OS.Modules.Identity.Contracts.Grants;
using IntoChat.OS.Modules.Identity.Rules;

namespace IntoChat.OS.Tests.Unit.Authority;

// A /try guest is marked by its principal alone: the try- prefix is reserved, a session for it is never a person, and
// GuestSurface is the whole list of what it may use.
public sealed class GuestSurfaceFacts
{
    private const string Guest = "try-0a1b2c3d4e5f";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // What the host registers for the session edge: the authority it stamps through and the ambient it reads back.
    private static readonly IServiceProvider EdgeServices = new ServiceCollection()
        .AddSingleton(IntoChat.OS.Tests.TestEdge.Authority).AddSingleton(IntoChat.OS.Tests.TestEdge.Callers).BuildServiceProvider();

    [Fact]
    public void AGuest_IsKnownByItsPrincipal_WhateverAppItIsStampedAs()
    {
        var guest = IntoChat.OS.Tests.TestEdge.Authority.Person(Guest, Guest, new BrainId(Guest));

        Assert.True(GuestSurface.Is(guest));
        Assert.True(GuestSurface.Is(IntoChat.OS.Tests.TestEdge.Authority.Guest(Guest, Guest, new BrainId(Guest))));
        Assert.True(GuestSurface.Is(McpCaller.AsAgent(IntoChat.OS.Tests.TestEdge.Authority, guest)));
    }

    [Theory]
    [InlineData("alice")]
    [InlineData("owner")]
    [InlineData("Try-0a1b2c3d4e5f")]
    [InlineData("retry-0a1b2c3d4e5f")]
    [InlineData("try")]
    [InlineData("u-try-0a1b2c3d4e5f")]
    public void AnyOtherPrincipal_IsNotAGuest_EvenInATryBrain(string principalId)
        => Assert.False(GuestSurface.Is(IntoChat.OS.Tests.TestEdge.Authority.Person(principalId, principalId, new BrainId(Guest))));

    [Fact]
    public void TheGuestApp_IsAnApp_AndNoOtherCallersApp()
    {
        var stamped = IntoChat.OS.Tests.TestEdge.Authority.Guest(Guest, Guest, new BrainId(Guest));

        Assert.Equal(GuestSurface.AppId, Assert.IsType<Actor.App>(stamped.Actor).Key);
        Assert.NotEqual(CallerApps.Assistant, GuestSurface.AppId);
        Assert.DoesNotContain('/', GuestSurface.AppId);
    }

    [Theory]
    [InlineData("screen_list")]
    [InlineData("screen_read")]
    [InlineData("screen_act")]
    public void AGuest_MayUseTheThreeScreenTools(string tool) => Assert.True(GuestSurface.MayUseTool(tool));

    [Theory]
    [InlineData("screen_approve")]
    [InlineData(NeuronSurfaceTools.CallName)]
    [InlineData(NeuronSurfaceTools.ListName)]
    [InlineData(NeuronSurfaceTools.SearchName)]
    [InlineData(NeuronSurfaceTools.GetName)]
    [InlineData(NeuronSurfaceTools.ContextName)]
    [InlineData("Screen_List")]
    [InlineData(" screen_list")]
    [InlineData("screen_list ")]
    [InlineData("screen_list\0")]
    [InlineData("")]
    [InlineData(null)]
    public void AGuest_MayUseNoOtherTool(string? tool) => Assert.False(GuestSurface.MayUseTool(tool));

    [Theory]
    [InlineData("photocraft")]
    [InlineData("helloworld")]
    [InlineData("btcalert")]
    public void AGuest_MayOpenTheShippedDemoScreens(string appId) => Assert.True(GuestSurface.MayOpen(appId));

    [Theory]
    [InlineData("creator")]
    [InlineData("ino")]
    [InlineData("settings")]
    [InlineData("confirmations")]
    [InlineData("PhotoCraft")]
    [InlineData(" photocraft")]
    [InlineData("photocraft ")]
    [InlineData("photocraft/../creator")]
    [InlineData("photocraft/creator")]
    [InlineData("apps/photocraft")]
    [InlineData("app.photocraft")]
    [InlineData("try-0a1b2c3d4e5f/apps/photocraft")]
    [InlineData("")]
    [InlineData(null)]
    public void AGuest_MayOpenNothingElse_HoweverItIsSpelled(string? appId) => Assert.False(GuestSurface.MayOpen(appId));

    [Theory]
    [InlineData(Guest)]
    [InlineData("try-")]
    [InlineData("try-alice")]
    public void ATryPrincipal_IsNeverAvailableToRegister(string principalId)
        => Assert.True(new PrincipalIdIsAvailable(principalId, ExistingPrincipal: null).Broken);

    [Theory]
    [InlineData("alice")]
    [InlineData("trying")]
    [InlineData("retry-1")]
    [InlineData("u-0a1b2c3d4e5f")]
    public void OtherPrincipals_StayAvailable(string principalId)
        => Assert.False(new PrincipalIdIsAvailable(principalId, ExistingPrincipal: null).Broken);

    [Fact]
    public async Task PasswordRegistration_RefusesATryUsername_BeforeItStoresACredential()
    {
        // No collaborator is reached: the username is refused first.
        var passwords = new PrincipalPasswords(null!, null!, null!);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => passwords.Register("try-alice", "correct-horse-battery-staple", "Alice"));
        Assert.Equal("That username is unavailable.", refused.Message);
    }

    [Fact]
    public void ASessionForATryPrincipal_ResolvesAsANonPerson()
    {
        var http = new DefaultHttpContext { RequestServices = EdgeServices, User = AccountSession.SessionUser(IntoChat.OS.Tests.TestEdge.Authority.Person(Guest, Guest, new BrainId(Guest))) };

        var caller = AccountSession.ResolveCaller(http, new AuthOptions { Posture = IdentityPosture.Secured });

        Assert.NotNull(caller);
        Assert.Equal(Guest, caller.For?.Principal);
        Assert.Equal(GuestSurface.AppId, Assert.IsType<Actor.App>(caller.Actor).Key);
    }

    [Fact]
    public void ASessionForAnyOtherPrincipal_StillResolvesAsThePerson()
    {
        var http = new DefaultHttpContext { RequestServices = EdgeServices, User = AccountSession.SessionUser(IntoChat.OS.Tests.TestEdge.Authority.Person("alice", "alice", new BrainId("owner"))) };

        var caller = AccountSession.ResolveCaller(http, new AuthOptions { Posture = IdentityPosture.Secured });

        Assert.NotNull(caller);
        Assert.IsType<Actor.Person>(caller.Actor);
    }

    [Fact]
    public async Task AGuestSession_PassesOnlyAnEndpointMarkedAsADoor_AndIsTheGuestAppThere()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Production",
            ApplicationName = typeof(GuestSurfaceFacts).Assembly.GetName().Name,
            ContentRootPath = Path.GetTempPath(),
        });
        builder.WebHost.UseSetting(WebHostDefaults.PreventHostingStartupKey, "true");
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { [AuthOptions.PostureKey] = IdentityPosture.Secured.ToString() });
        builder.Services.AddOptions<AuthOptions>().BindConfiguration(AuthOptions.SectionName);
        builder.Services.AddSingleton(IntoChat.OS.Tests.TestEdge.Authority);
        builder.Services.AddSingleton(IntoChat.OS.Tests.TestEdge.Callers);

        await using var app = builder.Build();
        // A guest identity made some other way than by the try middleware, on every route.
        app.Use((context, next) =>
        {
            context.User = AccountSession.SessionUser(IntoChat.OS.Tests.TestEdge.Authority.Person(Guest, Guest, new BrainId(Guest)));
            return next(context);
        });
        app.UseAccountSession();
        app.MapGet("/brains/{brainId}/door", Probe).WithMetadata(GuestSurface.Door.Instance);
        app.MapGet("/brains/{brainId}/shut", Probe);
        await app.StartAsync(Ct);
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };

        using var shut = await client.GetAsync($"/brains/{Guest}/shut", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, shut.StatusCode);

        var admitted = await client.GetFromJsonAsync<CallerProbe>($"/brains/{Guest}/door", Ct);
        Assert.NotNull(admitted);
        Assert.Equal(Guest, admitted.PrincipalId);
        Assert.Equal(Guest, admitted.BrainId);
        Assert.Equal(nameof(Actor.App), admitted.Kind);
        Assert.Equal(GuestSurface.AppId, admitted.AppId);
        Assert.False(admitted.IsActorPerson);

        await app.StopAsync(Ct);
    }

    [Theory]
    [InlineData("screen_list")]
    [InlineData("screen_read")]
    [InlineData("screen_act")]
    public async Task Mcp_StampsAGuestAsTheAgent_ForAGuestTool(string tool)
    {
        await using var os = DigitalBrain.Testing.TestOS.Local();

        var stamped = GuestMcp(os).Stamp(tool);

        Assert.IsType<Actor.Agent>(stamped.Actor);
        Assert.Equal("mcp", stamped.Conversation);
        Assert.Equal(Guest, stamped.For?.Principal);
        Assert.True(GuestSurface.Is(stamped));
    }

    [Theory]
    [InlineData("screen_approve")]
    [InlineData(NeuronSurfaceTools.CallName)]
    [InlineData(NeuronSurfaceTools.ListName)]
    [InlineData(NeuronSurfaceTools.SearchName)]
    [InlineData(NeuronSurfaceTools.GetName)]
    [InlineData(NeuronSurfaceTools.ContextName)]
    [InlineData("Screen_List")]
    [InlineData("")]
    public async Task Mcp_RefusesAGuest_AnyOtherTool(string tool)
    {
        await using var os = DigitalBrain.Testing.TestOS.Local();

        var refused = Assert.Throws<UnauthorizedAccessException>(() => GuestMcp(os).Stamp(tool));
        Assert.StartsWith("A guest session cannot use", refused.Message, StringComparison.Ordinal);
    }

    private static McpCaller GuestMcp(DigitalBrain.Kernel.IBrainObserver kernel)
    {
        var http = new DefaultHttpContext { RequestServices = EdgeServices, User = AccountSession.SessionUser(IntoChat.OS.Tests.TestEdge.Authority.Person(Guest, Guest, new BrainId(Guest))) };
        return new McpCaller(new HttpContextAccessor { HttpContext = http }, Options.Create(new AuthOptions { Posture = IdentityPosture.Secured }), kernel, IntoChat.OS.Tests.TestEdge.Authority);
    }

    private static IResult Probe(HttpContext http)
    {
        var caller = HttpCaller.Current(http) ?? throw new UnauthorizedAccessException("The edge stamped no caller.");
        return Results.Ok(new CallerProbe(caller.For?.Principal ?? "", caller.Brain.Value, caller.Actor.GetType().Name, (caller.Actor as Actor.App)?.Key, caller.Actor is Actor.Person));
    }

    private sealed record CallerProbe(string PrincipalId, string BrainId, string Kind, string? AppId, bool IsActorPerson);
}
