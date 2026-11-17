using System.Net;
using System.Net.Http.Json;
using DigitalBrain.Access;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Runtime.Orleans.Capabilities;
using IntoChat.OS.Authority.Identity;
using IntoChat.OS.Authority.Identity.Rules;
using IntoChat.OS.Connections.Identity.Configuration;
using IntoChat.OS.Connections.Surface;
using IntoChat.OS.Framework;
using IntoChat.OS.Kernel.Mcp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Tests.Unit.Authority;

// A /try guest is marked by its principal alone: the try- prefix is reserved, a session for it is never a person, and
// GuestSurface is the whole list of what it may use.
public sealed class GuestSurfaceFacts
{
    private const string Guest = "try-0a1b2c3d4e5f";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void AGuest_IsKnownByItsPrincipal_WhateverAppItIsStampedAs()
    {
        var guest = CapabilityCallers.Of(Guest, Guest, Guest);

        Assert.True(GuestSurface.Is(guest));
        Assert.True(GuestSurface.Is(guest with { AppId = GuestSurface.AppId }));
        Assert.True(GuestSurface.Is(McpCaller.AsAgent(guest)));
    }

    [Theory]
    [InlineData("alice")]
    [InlineData("owner")]
    [InlineData("Try-0a1b2c3d4e5f")]
    [InlineData("retry-0a1b2c3d4e5f")]
    [InlineData("try")]
    [InlineData("u-try-0a1b2c3d4e5f")]
    public void AnyOtherPrincipal_IsNotAGuest_EvenInATryBrain(string principalId)
        => Assert.False(GuestSurface.Is(CapabilityCallers.Of(principalId, principalId, Guest)));

    [Fact]
    public void TheGuestApp_IsAnApp_AndNoOtherCallersApp()
    {
        var stamped = CapabilityCallers.Of(Guest, Guest, Guest) with { AppId = GuestSurface.AppId };

        Assert.Equal(CallerKind.App, stamped.Kind);
        Assert.IsNotType<Actor.Person>(IntoChat.OS.Tests.TestEdge.From(stamped).Actor);
        Assert.DoesNotContain(GuestSurface.AppId,
            new[] { CapabilityCallers.PlatformApp, CapabilityCallers.SchedulerApp, CapabilityCallers.AssistantApp });
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
        var http = new DefaultHttpContext { User = AccountSession.SessionUser(CapabilityCallers.Of(Guest, Guest, Guest)) };

        var caller = AccountSession.ResolveCaller(http, new AuthOptions { Posture = IdentityPosture.Secured });

        Assert.NotNull(caller);
        Assert.Equal(Guest, caller.PrincipalId);
        Assert.NotEqual(CallerKind.User, caller.Kind);
        Assert.Equal(GuestSurface.AppId, caller.AppId);
        Assert.IsNotType<Actor.Person>(IntoChat.OS.Tests.TestEdge.From(caller).Actor);
    }

    [Fact]
    public void ASessionForAnyOtherPrincipal_StillResolvesAsThePerson()
    {
        var http = new DefaultHttpContext { User = AccountSession.SessionUser(CapabilityCallers.Of("alice", "alice", "owner")) };

        var caller = AccountSession.ResolveCaller(http, new AuthOptions { Posture = IdentityPosture.Secured });

        Assert.NotNull(caller);
        Assert.Equal(CallerKind.User, caller.Kind);
        Assert.Null(caller.AppId);
        Assert.IsType<Actor.Person>(IntoChat.OS.Tests.TestEdge.From(caller).Actor);
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

        await using var app = builder.Build();
        // A guest identity made some other way than by the try middleware, on every route.
        app.Use((context, next) =>
        {
            context.User = AccountSession.SessionUser(CapabilityCallers.Of(Guest, Guest, Guest));
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
        Assert.Equal(CallerKind.App.ToString(), admitted.Kind);
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

        Assert.Equal(CallerKind.Agent, stamped.Kind);
        Assert.Equal(CapabilityCallers.AssistantApp, stamped.AppId);
        Assert.Equal("mcp", stamped.ConversationId);
        Assert.Equal(Guest, stamped.PrincipalId);
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
        var http = new DefaultHttpContext { User = AccountSession.SessionUser(CapabilityCallers.Of(Guest, Guest, Guest)) };
        return new McpCaller(new HttpContextAccessor { HttpContext = http }, Options.Create(new AuthOptions { Posture = IdentityPosture.Secured }), kernel);
    }

    private static IResult Probe()
    {
        var caller = RuntimeCallerContext.Require();
        return Results.Ok(new CallerProbe(caller.PrincipalId, caller.BrainId, caller.Kind.ToString(), caller.AppId, IntoChat.OS.Tests.TestEdge.From(caller).Actor is Actor.Person));
    }

    private sealed record CallerProbe(string PrincipalId, string BrainId, string Kind, string? AppId, bool IsActorPerson);
}
