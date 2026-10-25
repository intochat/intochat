using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Modules.Flutter.Button.Signals;
using Microsoft.Extensions.DependencyInjection;
using static IntoChat.OS.Modules.Circles.Tests.CircleBrainRooms;

namespace IntoChat.OS.Modules.Circles.Tests;

// 06-07b (R4-17): the publication button installs only for a signed-in person stamped on the call, and the UI event's caller
// must name that same person; the event's caller alone never authorizes.
public sealed class PublicationButtonCallerFacts
{
    [Trait("host", "own")]
    [Fact]
    public async Task AnInstallNeedsTheStampedPersonAndAMatchingEventCaller()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start(ct, builder => builder.WithModule<FlutterModule>());
        var vlad = await brain.PersonAsync("vlad", ct);
        var other = await brain.PersonAsync("other", ct);
        var grains = brain.Services.GetRequiredService<IGrainFactory>();
        var app = IApp.Key(vlad.BrainId, "circle");
        var click = UiEvent.Of(new ButtonClicked(app + "/fork-button", "{}", Guid.NewGuid()));
        UiEventCaller As(IntoChat.OS.Testing.Person person) => new(person.PrincipalId, person.AccountId, person.BrainId, "User", "AuthenticatedHttp", null, null);
        var installs = 0;
        Task<AppSnapshot> Install(AppPublicationRef reference, string name, Guid operation) { installs++; throw new InvalidOperationException("not reached"); }
        Task Handle(UiEvent uiEvent) => PublicationButton.Handle(grains, brain.Services.GetRequiredService<IntoChat.OS.Runtime.IPlainNeurons>(), vlad.BrainId, "circle", "fork-button", uiEvent, Install);

        // No stamped caller: a forged event caller does not stand in for one.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Handle(click with { Caller = As(vlad) }));
        using (vlad.AsCaller())
        {
            // A dispatch that lost its stamp, or carries another person's, is refused too.
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Handle(click));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Handle(click with { Caller = As(other) }));
        }
        Assert.Equal(0, installs);
    }
}
