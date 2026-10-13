using System.Text.Json;
using DigitalBrain.Apps;
using DigitalBrain.Flutter;
using DigitalBrain.Flutter.Button;
using DigitalBrain.Flutter.Form;
using DigitalBrain.Flutter.Text;
using DigitalBrain.Flutter.Text.Signals;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using static DigitalBrain.Circles.Tests.CircleBrainRooms;

namespace DigitalBrain.Circles.Tests;

public sealed class PublicationButtonFacts
{
    [Fact]
    public async Task AnotherClickResumesAFailedImportWithTheSameReceiptAfterReactivation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start(ct, builder => builder.WithModule<FlutterModule>().ConfigureServices(services =>
        {
            services.RemoveAll<IAppRuntime>();
            services.AddSingleton<IAppRuntime, FailFirstCopy>();
        }));
        var vlad = await brain.PersonAsync("vlad", ct);
        await Own(brain, vlad, "echo", files: new()
        {
            ["preview.txt"] = "A",
            ["go.cs"] = NotCompiling.Replace("DoesNotCompile();", "System.GC.KeepAlive(request);", StringComparison.Ordinal),
        });
        var source = brain.Get<IApp>(IApp.Key(vlad.BrainId, "echo"));
        await vlad.In(vlad.BrainId, () => source.Publish());
        var release = Assert.Single(await source.PublishedReleases()).Reference;
        var app = IApp.Key(vlad.BrainId, "circle");
        var factory = brain.Get<ICircleFactory>(ICircleFactory.Key(vlad.BrainId));
        var button = brain.Get<IButton>(app + "/fork-button");
        var status = brain.Get<IText>(app + "/status");
        using var caller = vlad.AsCaller();
        await factory.BindPublicationButton();
        var form = brain.Get<IForm>(app + "/forms/" + release.Digest);
        await form.Define(new("Copy", [new("name", "Name", FormFieldKind.PlainText)]));
        await form.SetDraft("name", "retry-copy");
        await button.Set("Fork", JsonSerializer.Serialize(release, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        await using var changed = await brain.Observe<TextChanged>(status, ct);
        await button.Click();
        await changed.NextAsync(ct: ct);
        Assert.StartsWith("Could not install:", (await status.Read()).Markdown);
        var copy = brain.Get<IApp>(IApp.Key(vlad.BrainId, "retry-copy"));
        var receipt = (await copy.Read()).Installation;
        Assert.NotNull(receipt);
        await brain.DeactivateAsync(factory, ct);
        await button.Click();
        await changed.NextAsync(ct: ct);
        Assert.StartsWith("Installed retry-copy", (await status.Read()).Markdown);
        Assert.Equal(receipt, (await copy.Read()).Installation);
        Assert.Equal(AppStatus.Live, (await copy.Read()).Status);
    }

    private sealed class FailFirstCopy : IAppRuntime
    {
        private bool _failed;
        public Task<IReadOnlyDictionary<string, string>> Deploy(string appKey, Dictionary<string, string> sources,
            Dictionary<string, string> configuration, CancellationToken cancellationToken)
        {
            if (appKey.EndsWith("/retry-copy", StringComparison.Ordinal) && !_failed)
            { _failed = true; throw new InvalidOperationException("Injected deployment failure."); }
            return Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());
        }
        public Task Retire(string appKey, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Fact]
    public async Task TheBringButtonInstallsAsTheClickingMemberAndKeepsTheSelectedRelease()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start(ct, builder => builder.WithModule<FlutterModule>());
        var vlad = await brain.PersonAsync("vlad", ct);
        var member = await brain.PersonAsync("member", ct);
        await Own(brain, vlad, "echo", files: new() { ["preview.txt"] = "A" });
        var source = brain.Get<IApp>(IApp.Key(vlad.BrainId, "echo"));
        await vlad.In(vlad.BrainId, () => source.Publish());
        var release = Assert.Single(await source.PublishedReleases()).Reference;
        var room = await Create(brain, vlad);
        await Join(brain, room, vlad, member);
        var app = IApp.Key(room, "circleroom");
        var button = brain.Get<IButton>(app + "/bring-button");
        var status = brain.Get<IText>(app + "/status");
        await vlad.In(room, async () =>
        {
            var form = brain.Get<IForm>(app + "/forms/" + release.Digest);
            await form.Define(new("Copy", [new("name", "Name", FormFieldKind.PlainText)]));
            await form.SetDraft("name", "member-copy");
            await button.Set("Bring", JsonSerializer.Serialize(release, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            await Circle(brain, room).BindPublicationButton();
        });
        await using var changed = await brain.Observe<TextChanged>(status, ct);
        await member.In(room, button.Click);
        await changed.NextAsync(ct: ct);
        var copy = await member.In(room, () => brain.Get<IApp>(IApp.Key(room, "member-copy")).Read());
        Assert.Equal(member.AccountId, copy.Origin?.InstalledByAccount);
        Assert.Equal("vlad", copy.Origin?.Publisher);
        Assert.Equal(release.Digest, copy.Origin?.PublicationDigest);
        Assert.Equal("A", copy.Files["preview.txt"]);
        Assert.Contains("Needs setup", (await member.In(room, status.Read)).Markdown);
        await vlad.In(room, () => Circle(brain, room).Remove(member.AccountId));
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => member.In(room, button.Click));
    }
}
